using System.Globalization;
using System.Text.RegularExpressions;

namespace Ostatok.Api.Domain;

public enum ImportRowStatus { Ok, Warning, Error }

/// <param name="Line">Номер строки в файле, считая заголовок первой.</param>
/// <param name="Price">Сумма по строке: из файла, а если её там нет — количество × цена из справочника.</param>
/// <param name="Cost">
/// Цена за единицу по файлу (сумма ÷ количество): после приёмки она станет закупочной ценой продукта.
/// Пусто, если суммы в файле нет или продукт — заготовка (её цена считается по составу).
/// </param>
/// <param name="CurrentCost">Закупочная цена продукта в справочнике сейчас.</param>
public sealed record ImportRow(
    int Line, string ProductText, Guid? ProductId, decimal? Quantity, Unit? Unit, int? ShelfLifeHours, decimal? Price,
    ImportRowStatus Status, string Message, decimal? Cost = null, decimal? CurrentCost = null);

/// <summary>
/// Разбор файла поставки: таблица с колонками «продукт; количество; единица; срок хранения; цена».
/// Ничего не сохраняет — возвращает строки с результатом проверки, чтобы человек подтвердил загрузку.
/// </summary>
public static partial class SupplyImport
{
    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)\s*(ч|час\w*|д|дн|день|дня|дней|сут\w*)?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShelfLife();

    public static IReadOnlyList<ImportRow> Parse(string text, IReadOnlyCollection<Product> products)
    {
        // Больше пяти тысяч строк в одной накладной не бывает; лишнее не разбирается.
        var lines = text.Split('\n').Take(MaxRows + 1).Select(l => l.TrimEnd('\r')).ToList();
        // Русский Excel сохраняет CSV через точку с запятой, остальные программы — через запятую.
        var separator = lines[0].Contains(';') ? ';' : ',';
        return ParseRows(lines.Select(l => l.Split(separator)).ToList(), products);
    }

    /// <summary>Разбор уже разделённой на ячейки таблицы; первая строка — заголовки колонок.</summary>
    public static IReadOnlyList<ImportRow> ParseRows(IReadOnlyList<string[]> table, IReadOnlyCollection<Product> products)
    {
        var rows = new List<ImportRow>();
        for (var i = 1; i < table.Count; i++)
        {
            var cells = table[i].Select(c => c.Trim().Trim('"')).ToArray();
            if (cells.All(c => c == "")) continue;
            rows.Add(ParseRow(i + 1, cells, products));
        }
        return rows;
    }

    private static ImportRow ParseRow(int line, string[] cells, IReadOnlyCollection<Product> products)
    {
        string Cell(int index) => index < cells.Length ? cells[index] : "";
        var (name, quantityText, unitText, lifeText, priceText) = (Cell(0), Cell(1), Cell(2), Cell(3), Cell(4));

        ImportRow Fail(string message, Guid? productId = null) =>
            new(line, name, productId, null, null, null, null, ImportRowStatus.Error, message);

        if (name == "") return Fail("Не указан продукт.");
        var product = products.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            // Подсказку «может быть, …» ищем только для названий обычной длины: сравнение растёт с длиной строки.
            var closest = name.Length > 120 ? null : products.MinBy(p => Distance(p.Name.ToLowerInvariant(), name.ToLowerInvariant()));
            var similar = closest is not null && Distance(closest.Name.ToLowerInvariant(), name.ToLowerInvariant()) <= Math.Max(2, name.Length / 3);
            return Fail("Такого продукта нет в справочнике." + (similar ? $" Может быть, «{closest!.Name}»?" : ""));
        }

        if (!TryNumber(quantityText, out var quantity) || quantity <= 0)
            return Fail($"Количество «{quantityText}» не число. Укажите число, а единицу — в отдельной колонке.", product.Id);

        if (unitText != "" && ParseUnit(unitText) != product.Unit)
            return Fail($"Единица «{unitText}» не совпадает со справочником: {product.Name} учитывается в {UnitName(product.Unit)}.", product.Id);

        var fromFile = TryNumber(priceText.Replace("₽", "").Replace(" ", "").Replace(" ", ""), out var p) && p > 0 ? p : (decimal?)null;
        var price = fromFile ?? quantity * product.Cost;
        var cost = fromFile is null || product.PrepRecipe.Count > 0 ? (decimal?)null : Math.Round(fromFile.Value / quantity, 2);
        // Сумма меньше копейки за единицу — не цена: справочник не меняется.
        if (cost == 0) cost = null;

        if (lifeText is "" or "—" or "-")
        {
            return new ImportRow(line, name, product.Id, quantity, product.Unit, product.ShelfLifeHours, price, ImportRowStatus.Warning,
                $"Срок хранения пустой. Подставим норматив из справочника: {Ru.Duration(product.ShelfLifeHours)}.", cost, product.Cost);
        }

        var match = ShelfLife().Match(lifeText);
        if (!match.Success || !TryNumber(match.Groups[1].Value, out var amount) || amount <= 0)
            return Fail($"Срок хранения «{lifeText}» не распознан. Укажите часы или дни, например «72 ч» или «5 дней».", product.Id);

        var unit = match.Groups[2].Value;
        // Дробное число без единицы — скорее дата без года («15.10»), чем срок в часах.
        if (unit == "" && match.Groups[1].Value.IndexOfAny(['.', ',']) >= 0)
            return Fail($"Срок хранения «{lifeText}» не распознан. Укажите часы или дни, например «72 ч» или «5 дней».", product.Id);
        var inDays = unit.ToLowerInvariant() is var u && (u.StartsWith('д') || u.StartsWith("сут"));
        var hours = (int)Math.Round(amount * (inDays ? 24 : 1));
        // Меньше часа после округления поставка не примет: ошибка видна уже в предпросмотре.
        if (hours < 1) return Fail($"Срок хранения «{lifeText}» меньше часа. Укажите срок от 1 часа.", product.Id);
        return new ImportRow(line, name, product.Id, quantity, product.Unit, hours, price, ImportRowStatus.Ok, "", cost, product.Cost);
    }

    private static bool TryNumber(string text, out decimal value) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static Unit? ParseUnit(string text) => text.ToLowerInvariant().TrimEnd('.') switch
    {
        "кг" or "kg" => Unit.Kg,
        "л" or "l" => Unit.L,
        "шт" or "штук" or "pcs" => Unit.Pcs,
        _ => null,
    };

    public const int MaxRows = 5000;

    private static string UnitName(Unit unit) => unit switch { Unit.Kg => "кг", Unit.L => "л", _ => "шт" };

    /// <summary>Расстояние Левенштейна — для подсказки «может быть, вы имели в виду».</summary>
    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
