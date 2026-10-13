using Ostatok.Api.Domain;

namespace Ostatok.Tests;

public class SupplyImportTests
{
    private static readonly Product[] Products =
    [
        new() { Id = Guid.NewGuid(), Name = "Лосось охлаждённый", Unit = Unit.Kg, ShelfLifeHours = 48, Cost = 1900 },
        new() { Id = Guid.NewGuid(), Name = "Сливки 33%", Unit = Unit.L, ShelfLifeHours = 120, Cost = 480 },
        new() { Id = Guid.NewGuid(), Name = "Яйцо куриное", Unit = Unit.Pcs, ShelfLifeHours = 480, Cost = 14 },
    ];

    private static IReadOnlyList<ImportRow> Parse(params string[] rows) =>
        SupplyImport.Parse(string.Join("\r\n", ["продукт;количество;единица;срок хранения;цена", .. rows]), Products);

    [Fact]
    public void Правильная_строка_разбирается_целиком()
    {
        var row = Assert.Single(Parse("Лосось охлаждённый;3;кг;72 ч;5 550 ₽"));

        Assert.Equal(
            (2, Products[0].Id, 3m, Unit.Kg, 72, 5550m, ImportRowStatus.Ok),
            (row.Line, row.ProductId!.Value, row.Quantity!.Value, row.Unit!.Value, row.ShelfLifeHours!.Value, row.Price!.Value, row.Status));
    }

    [Fact]
    public void Срок_в_днях_переводится_в_часы_а_количество_принимается_с_запятой()
    {
        var row = Assert.Single(Parse("яйцо куриное;120;шт;25 дней;"));

        Assert.Equal((600, 120m, 1680m), (row.ShelfLifeHours!.Value, row.Quantity!.Value, row.Price!.Value));
        Assert.Equal(2.5m, Assert.Single(Parse("Сливки 33%;2,5;л;5 дн;")).Quantity);
    }

    [Fact]
    public void Пустой_срок_заменяется_нормативом_с_предупреждением()
    {
        var row = Assert.Single(Parse("Сливки 33%;3;л;—;1 560 ₽"));

        Assert.Equal((ImportRowStatus.Warning, 120), (row.Status, row.ShelfLifeHours!.Value));
        Assert.Contains("норматив из справочника: 5 дн", row.Message);
    }

    [Fact]
    public void Ошибки_называют_что_не_так_в_строке()
    {
        var rows = Parse(
            "Лосось охлажденный;2;кг;72 ч;",
            "Сливки 33%;2.5л;л;72 ч;",
            "Сливки 33%;2;кг;72 ч;",
            "Трюфель;1;кг;72 ч;",
            "Яйцо куриное;10;шт;скоро;");

        Assert.All(rows, r => Assert.Equal(ImportRowStatus.Error, r.Status));
        Assert.Contains("Может быть, «Лосось охлаждённый»?", rows[0].Message);
        Assert.Contains("«2.5л» не число", rows[1].Message);
        Assert.Contains("учитывается в л", rows[2].Message);
        Assert.Equal("Такого продукта нет в справочнике.", rows[3].Message);
        Assert.Contains("«скоро» не распознан", rows[4].Message);
    }

    [Fact]
    public void Понимает_запятую_как_разделитель_и_пропускает_пустые_строки()
    {
        var rows = SupplyImport.Parse("product,qty,unit,life,price\nЛосось охлаждённый,3,кг,72 ч,\n\n", Products);

        Assert.Equal(ImportRowStatus.Ok, Assert.Single(rows).Status);
    }

    [Fact]
    public void Дата_без_года_и_срок_меньше_часа_не_принимаются_за_срок()
    {
        // «15.10» — дата, а не 15 часов; «0,4 ч» округлилось бы до нуля, и поставку отклонил бы уже сервер.
        Assert.All(Parse("Лосось охлаждённый;3;кг;15.10;", "Лосось охлаждённый;3;кг;0,4 ч;"), row => Assert.Equal(ImportRowStatus.Error, row.Status));
        // Целое число без единицы — по-прежнему часы.
        Assert.Equal(72, Assert.Single(Parse("Лосось охлаждённый;3;кг;72;")).ShelfLifeHours);
    }
}
