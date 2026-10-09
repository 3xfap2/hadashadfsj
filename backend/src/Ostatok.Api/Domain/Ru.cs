using System.Globalization;

namespace Ostatok.Api.Domain;

/// <summary>Русские подписи для объяснений. Без зависимости от культуры сервера: в контейнере её может не быть.</summary>
public static class Ru
{
    public static string Qty(decimal quantity, Unit unit) =>
        $"{quantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',')} {UnitName(unit)}";

    public static string Money(decimal value) =>
        $"{Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ')} ₽";

    public static string Duration(double hours) => hours switch
    {
        < 1 => $"{Math.Max(1, (int)Math.Round(hours * 60))} мин",
        < 48 => $"{(int)Math.Round(hours)} ч",
        _ => $"{(int)Math.Round(hours / 24)} дн",
    };

    public static string Portions(int n) => $"{n} {Plural(n, "порцию", "порции", "порций")}";

    public static string Plural(int n, string one, string few, string many)
    {
        var (tens, ones) = (n % 100 / 10, n % 10);
        if (tens == 1) return many;
        return ones switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    private static string UnitName(Unit unit) => unit switch
    {
        Unit.Kg => "кг",
        Unit.L => "л",
        _ => "шт",
    };
}
