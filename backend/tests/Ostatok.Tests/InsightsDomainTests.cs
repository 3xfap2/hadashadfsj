using Ostatok.Api.Domain;

namespace Ostatok.Tests;

/// <summary>Маленький склад для тестов алгоритмов: продукты, партии и блюда задаются одной строкой.</summary>
internal sealed class Kitchen
{
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public List<Batch> Batches { get; } = [];
    public List<Dish> Dishes { get; } = [];
    public List<Product> Products { get; } = [];

    public Product Product(string name, decimal cost = 1000)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = name, Unit = Unit.Kg, ShelfLifeHours = 48, Cost = cost };
        Products.Add(product);
        return product;
    }

    public Kitchen Stock(Product product, decimal quantity, double expiresInHours)
    {
        Batches.Add(new Batch
        {
            Id = Guid.NewGuid(), ProductId = product.Id, Product = product,
            Quantity = quantity, Remaining = quantity, ExpiresAt = Now.AddHours(expiresInHours),
        });
        return this;
    }

    public Dish Dish(string name, params (Product Product, decimal Quantity)[] recipe)
    {
        var dish = new Dish
        {
            Id = Guid.NewGuid(), Name = name, Price = 500,
            Recipe = recipe.Select(r => new RecipeItem { ProductId = r.Product.Id, Product = r.Product, Quantity = r.Quantity }).ToList(),
        };
        Dishes.Add(dish);
        return dish;
    }

    public ILookup<Guid, Batch> ByProduct => Batches.ToLookup(b => b.ProductId);

    public StockTotals Totals => new(
        Batches.Where(b => b.ExpiresAt > Now).GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.Sum(b => b.Remaining)),
        Batches.Where(b => b.ExpiresAt <= Now).Select(b => b.ProductId).ToHashSet());

    public IReadOnlyList<HotOffer> HotOffers(double horizonHours = 48) =>
        Ostatok.Api.Domain.HotOffers.Rank(Dishes, Batches, Totals, Now, horizonHours);
}

public class HotOffersTests
{
    [Fact]
    public void Чем_ближе_срок_продукта_тем_выше_блюдо()
    {
        var k = new Kitchen();
        var (soon, later) = (k.Product("Лосось"), k.Product("Креветки"));
        k.Stock(soon, 1, 4).Stock(later, 1, 30);
        k.Dish("Суп с креветками", (later, 0.1m));
        k.Dish("Стейк из лосося", (soon, 0.1m));

        Assert.Equal(["Стейк из лосося", "Суп с креветками"], k.HotOffers().Select(o => o.DishName));
    }

    [Fact]
    public void При_равной_срочности_выше_блюдо_которое_спасает_больше_денег()
    {
        var k = new Kitchen();
        var (cheap, expensive) = (k.Product("Томаты", cost: 300), k.Product("Вырезка", cost: 2400));
        k.Stock(cheap, 1, 10).Stock(expensive, 1, 10);
        k.Dish("Салат", (cheap, 0.2m));
        k.Dish("Филе миньон", (expensive, 0.2m));

        Assert.Equal(["Филе миньон", "Салат"], k.HotOffers().Select(o => o.DishName));
    }

    [Fact]
    public void Единственное_блюдо_с_продуктом_выше_чем_одно_из_нескольких()
    {
        var k = new Kitchen();
        var (shared, exclusive) = (k.Product("Романо"), k.Product("Основа том ям"));
        k.Stock(shared, 1, 10).Stock(exclusive, 1, 10);
        k.Dish("Цезарь с курицей", (shared, 0.2m));
        k.Dish("Цезарь с креветками", (shared, 0.2m));
        k.Dish("Том ям", (exclusive, 0.2m));

        var offers = k.HotOffers();

        Assert.Equal("Том ям", offers[0].DishName);
        Assert.Equal(1, offers[0].Ingredients[0].AlternativeDishes);
        Assert.Contains("Других блюд с этим продуктом в продаже нет", offers[0].Reason);
    }

    [Fact]
    public void Учитывается_только_то_что_блюдо_успеет_израсходовать()
    {
        var k = new Kitchen();
        var (salmon, cream) = (k.Product("Лосось", cost: 2000), k.Product("Сливки"));
        // Лосося на 10 порций, но сливок только на 2: больше двух порций не продать.
        k.Stock(salmon, 2, 6).Stock(cream, 0.1m, 100);
        k.Dish("Стейк", (salmon, 0.2m), (cream, 0.05m));

        var offer = Assert.Single(k.HotOffers());

        Assert.Equal((2, 2, 0.4m, 800m), (offer.Portions, offer.AvailablePortions, offer.Ingredients[0].UsableQuantity, offer.ValueAtRisk));
    }

    [Fact]
    public void Рекомендуется_столько_порций_сколько_нужно_чтобы_израсходовать_истекающее()
    {
        var k = new Kitchen();
        var salmon = k.Product("Лосось");
        // Истекает 0,7 кг, ещё 5 кг свежего: продавать нужно 4 порции, а не все 28.
        k.Stock(salmon, 0.7m, 5).Stock(salmon, 5, 200);
        k.Dish("Стейк", (salmon, 0.2m));

        var offer = Assert.Single(k.HotOffers());

        Assert.Equal((4, 28), (offer.Portions, offer.AvailablePortions));
        Assert.Contains("Лосось: 0,7 кг истекает через 5 ч. Продайте 4 порции", offer.Reason);
    }

    [Fact]
    public void Объяснение_называет_ближайшую_партию_и_общий_объём_под_угрозой()
    {
        var k = new Kitchen();
        var shrimp = k.Product("Креветки");
        k.Stock(shrimp, 1.2m, 6).Stock(shrimp, 2, 40);
        k.Dish("Том ям", (shrimp, 0.1m));

        var offer = Assert.Single(k.HotOffers());

        Assert.StartsWith("Креветки: 1,2 кг истекает через 6 ч (всего 3,2 кг за 2 дн). Продайте 32 порции", offer.Reason);
    }

    [Fact]
    public void Блюдо_в_стопе_и_продукты_с_запасом_по_сроку_не_предлагаются()
    {
        var k = new Kitchen();
        var (expiring, missing, fresh) = (k.Product("Лосось"), k.Product("Авокадо"), k.Product("Пармезан"));
        k.Stock(expiring, 1, 5).Stock(fresh, 1, 500);
        k.Dish("Боул", (expiring, 0.1m), (missing, 1));
        k.Dish("Сырная тарелка", (fresh, 0.1m));

        Assert.Empty(k.HotOffers());
    }

    [Fact]
    public void Просроченная_партия_в_расчёт_не_идёт()
    {
        var k = new Kitchen();
        var cream = k.Product("Сливки");
        k.Stock(cream, 5, -2).Stock(cream, 1, 300);
        k.Dish("Суп", (cream, 0.1m));

        Assert.Empty(k.HotOffers());
    }
}

public class ForecastTests
{
    private static readonly DateTimeOffset Now = Kitchen.Now;

    [Fact]
    public void Без_расхода_остаток_кончается_когда_истекает_последняя_партия()
    {
        var k = new Kitchen();
        var p = k.Product("Лосось");
        k.Stock(p, 1, 5).Stock(p, 1, 30);

        Assert.Equal(Now.AddHours(30), Forecast.RunOutAt(k.Batches, perHour: 0, threshold: 0.2m, Now));
    }

    [Fact]
    public void При_расходе_остаток_кончается_раньше_срока()
    {
        var k = new Kitchen();
        var p = k.Product("Лосось");
        k.Stock(p, 2.2m, 100);

        // 2 кг до порога в одну порцию при расходе 0,5 кг в час — 4 часа.
        Assert.Equal(Now.AddHours(4), Forecast.RunOutAt(k.Batches, perHour: 0.5m, threshold: 0.2m, Now));
    }

    [Fact]
    public void Истекающая_партия_сокращает_запас_даже_если_её_не_успели_израсходовать()
    {
        var k = new Kitchen();
        var p = k.Product("Лосось");
        k.Stock(p, 5, 2).Stock(p, 1, 100);

        // За 2 часа уйдёт 0,2 кг из ранней партии, остальные 4,8 кг пропадут; поздней партии хватит ещё на 10 часов.
        Assert.Equal(Now.AddHours(12), Forecast.RunOutAt(k.Batches, perHour: 0.1m, threshold: 0, Now));
    }

    [Fact]
    public void Прогноз_стопа_блюда_определяет_ингредиент_который_кончится_первым()
    {
        var k = new Kitchen();
        var (salmon, cream) = (k.Product("Лосось"), k.Product("Сливки"));
        k.Stock(salmon, 1, 5).Stock(cream, 1, 100);
        var dish = k.Dish("Стейк", (salmon, 0.2m), (cream, 0.05m));

        var at = Forecast.DishStopAt(dish, k.ByProduct, new Dictionary<Guid, decimal>(), Now, horizonHours: 72);

        Assert.Equal(Now.AddHours(5), at);
    }

    [Fact]
    public void За_горизонтом_прогноза_стопа_нет()
    {
        var k = new Kitchen();
        var p = k.Product("Пармезан");
        k.Stock(p, 1, 500);
        var dish = k.Dish("Сырная тарелка", (p, 0.1m));

        Assert.Null(Forecast.DishStopAt(dish, k.ByProduct, new Dictionary<Guid, decimal>(), Now, horizonHours: 72));
    }

    [Fact]
    public void Закупка_предлагается_когда_продукта_нет_и_когда_запаса_не_хватит_на_срок_покрытия()
    {
        var k = new Kitchen();
        var (missing, running, plenty) = (k.Product("Авокадо"), k.Product("Креветки"), k.Product("Картофель"));
        k.Stock(running, 2.1m, 200).Stock(plenty, 50, 500);
        k.Dish("Боул", (missing, 0.5m));
        k.Dish("Том ям", (running, 0.1m));
        k.Dish("Пюре", (plenty, 0.2m));
        var perHour = new Dictionary<Guid, decimal> { [running.Id] = 0.1m, [plenty.Id] = 0.1m };

        var suggestions = Forecast.Purchases(k.Products, k.Dishes, k.ByProduct, perHour, Now, coverHours: 72);

        Assert.Equal(["Авокадо", "Креветки"], suggestions.Select(s => s.ProductName));
        // Авокадо: истории нет — на 10 порций. Креветки: хватит на 20 ч, на оставшиеся 52 ч нужно 5,2 кг.
        Assert.Equal([5m, 5.2m], suggestions.Select(s => s.Quantity));
        Assert.Contains("В стоп-листе: Боул", suggestions[0].Reason);
        Assert.Contains("При расходе 2,4 кг в день остатка хватит на 20 ч", suggestions[1].Reason);
    }

    [Fact]
    public void Закупка_объясняет_когда_запас_пропадёт_по_сроку_а_не_по_расходу()
    {
        var k = new Kitchen();
        var p = k.Product("Сливки");
        k.Stock(p, 10, 6);
        k.Dish("Суп", (p, 0.1m));

        var suggestion = Assert.Single(Forecast.Purchases(k.Products, k.Dishes, k.ByProduct, new Dictionary<Guid, decimal> { [p.Id] = 0.1m }, Now, 72));

        Assert.Contains("истечёт раньше, чем израсходуется", suggestion.Reason);
        Assert.Equal(6.6m, suggestion.Quantity);
    }
}
