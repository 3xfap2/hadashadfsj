using Ostatok.Api.Domain;

namespace Ostatok.Tests;

/// <summary>Подбор замен блюдам отклонённого заказа — без базы.</summary>
public class AlternativesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly Product Scarce = new() { Id = Guid.NewGuid(), Name = "Лосось", Unit = Unit.Kg, Cost = 1500 };
    private static readonly Product Plenty = new() { Id = Guid.NewGuid(), Name = "Картофель", Unit = Unit.Kg, Cost = 50 };
    private static readonly Product Basil = new() { Id = Guid.NewGuid(), Name = "Базилик", Unit = Unit.Kg, Cost = 900 };

    private static Dish Dish(string name, decimal price, string? category, params (Product Product, decimal Quantity)[] recipe) => new()
    {
        Id = Guid.NewGuid(), Name = name, Price = price, Category = category,
        Recipe = recipe.Select(r => new RecipeItem { ProductId = r.Product.Id, Product = r.Product, Quantity = r.Quantity }).ToList(),
    };

    private static Batch Batch(Product product, decimal remaining, double hours) => new()
    {
        Id = Guid.NewGuid(), ProductId = product.Id, Product = product, Quantity = remaining, Remaining = remaining, ExpiresAt = Now.AddHours(hours),
    };

    private static IReadOnlyList<DishReplacement> Find(List<Dish> dishes, List<Batch> batches, Dictionary<Guid, int> order)
    {
        var stock = new StockTotals(
            batches.GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.Sum(b => b.Remaining)), new HashSet<Guid>());
        var hot = HotOffers.Rank(dishes, batches, stock, Now, new HotRules(), costs: false);
        return Alternatives.For(dishes, stock, hot, order, new HashSet<Guid> { Scarce.Id });
    }

    [Fact]
    public void Сначала_тот_же_раздел_затем_горячие_без_стопа_дорогих_и_блюд_из_заказа()
    {
        var failed = Dish("Стейк из лосося", 1000, "Горячее", (Scarce, 0.3m));
        var sameSection = Dish("Котлета с пюре", 900, "Горячее", (Plenty, 0.2m));
        var hot = Dish("Паста с песто", 800, "Паста", (Basil, 0.05m));
        var stopped = Dish("Лосось на гриле", 1100, "Горячее", (Scarce, 0.2m));
        var tooCheap = Dish("Картофель фри", 300, "Горячее", (Plenty, 0.2m));
        var inOrder = Dish("Драники", 950, "Горячее", (Plenty, 0.2m));
        // Отдельно хватает на две порции, но драники из заказа забирают картофель — остаётся на одну.
        var tight = Dish("Запеканка", 1000, "Горячее", (Plenty, 1m));
        var other = Dish("Овощное рагу", 990, "Овощи", (Plenty, 0.1m));
        var dishes = new List<Dish> { failed, sameSection, hot, stopped, tooCheap, inOrder, tight, other };
        var batches = new List<Batch> { Batch(Scarce, 0.1m, 40), Batch(Plenty, 2.1m, 200), Batch(Basil, 0.5m, 6) };

        var result = Find(dishes, batches, new Dictionary<Guid, int> { [failed.Id] = 2, [inOrder.Id] = 1 });

        var replacement = Assert.Single(result);
        Assert.Equal((failed.Id, 2), (replacement.DishId, replacement.Quantity));
        // Блюдо из другого раздела (не горячее) не предлагается: официанту нужна замена того же рода.
        Assert.Equal([sameSection.Id, hot.Id], replacement.Options.Select(o => o.DishId));
        Assert.Equal("тот же раздел, в продаже 9 порций", replacement.Options[0].Reason);
        Assert.True(replacement.Options[1].Hot);
        Assert.Equal("из горячих: базилик истекает через 6 ч", replacement.Options[1].Reason);
    }

    [Fact]
    public void Блюдо_снятое_с_продажи_получает_замены_и_само_не_предлагается()
    {
        var paused = Dish("Борщ", 400, "Супы", (Plenty, 0.1m));
        paused.ManuallyStopped = true;
        var soup = Dish("Щи", 380, "Супы", (Plenty, 0.1m));
        var otherPaused = Dish("Солянка", 420, "Супы", (Plenty, 0.1m));
        otherPaused.ManuallyStopped = true;
        var stock = new StockTotals(new Dictionary<Guid, decimal> { [Plenty.Id] = 5 }, new HashSet<Guid>());

        var result = Alternatives.For([paused, soup, otherPaused], stock, [], new Dictionary<Guid, int> { [paused.Id] = 1 }, new HashSet<Guid>());

        Assert.Equal([soup.Id], Assert.Single(result).Options.Select(o => o.DishId));
    }

    [Fact]
    public void Если_собрать_можно_всё_замен_нет()
    {
        var dish = Dish("Пюре", 200, null, (Plenty, 0.2m));
        var stock = new StockTotals(new Dictionary<Guid, decimal> { [Plenty.Id] = 5 }, new HashSet<Guid>());

        Assert.Empty(Alternatives.For([dish], stock, [], new Dictionary<Guid, int> { [dish.Id] = 1 }, new HashSet<Guid> { Scarce.Id }));
    }
}
