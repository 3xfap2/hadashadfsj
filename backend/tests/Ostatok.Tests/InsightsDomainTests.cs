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
        var product = new Product { Id = Guid.NewGuid(), Name = name, Unit = Unit.Kg, ShelfLifeHours = 240, Cost = cost };
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

    public IReadOnlyList<HotOffer> HotOffers(double horizonHours = 48, Dictionary<Guid, decimal>? perHour = null, HotRules? rules = null) =>
        Ostatok.Api.Domain.HotOffers.Rank(Dishes, Batches, Totals, Now, rules ?? new HotRules(horizonHours), perHour);
}

public class HotOffersTests
{
    [Fact]
    public void Истекающий_продукт_делится_между_блюдами_и_не_считается_дважды()
    {
        var k = new Kitchen();
        var (chicken, greens, cheese) = (k.Product("Курица", cost: 400), k.Product("Салат", cost: 500), k.Product("Сыр", cost: 800));
        // 2 кг курицы истекают через 6 часов. Салата хватает только на 4 цезаря, остальное — долгого хранения.
        k.Stock(chicken, 2, 6).Stock(greens, 0.4m, 200).Stock(cheese, 0.5m, 6);
        k.Dish("Цезарь", (chicken, 0.2m), (greens, 0.1m));
        k.Dish("Суп", (chicken, 0.25m));
        k.Dish("Сэндвич", (chicken, 0.1m));

        var plan = HotOffers.Plan(k.Dishes, k.Batches, k.Totals, Kitchen.Now, new HotRules());

        // Каждому блюду — своя доля: в сумме ровно 2 кг курицы, а не по 2 кг на каждое.
        var chickenShares = plan.Offers.SelectMany(o => o.Ingredients).Where(i => i.ProductId == chicken.Id).Sum(i => i.UsableQuantity);
        Assert.Equal(2m, chickenShares);
        Assert.All(plan.Offers, o => Assert.True(o.Portions > 0));
        Assert.Equal(plan.PlannedValue, plan.Offers.Sum(o => o.ValueAtRisk));
        // Суп идёт первым и забирает всю курицу: остальным блюдам спасать нечего, в списке их нет.
        Assert.Equal(["Суп"], plan.Offers.Select(o => o.DishName));
        Assert.Equal(8, plan.Offers[0].Portions);
        // Сыр истекает, но блюд с ним нет: он не пропадает из виду, а остаётся в плане отдельной строкой.
        Assert.Equal((2 * 400 + 0.5m * 800, 800m), (plan.AtRiskValue, plan.PlannedValue));
        Assert.Equal(("Сыр", 0.5m, 400m), plan.Leftovers.Select(l => (l.ProductName, l.Quantity, l.Value!.Value)).Single());
    }

    [Fact]
    public void Блюдо_которому_не_хватает_других_продуктов_оставляет_остаток_следующему()
    {
        var k = new Kitchen();
        var (chicken, greens) = (k.Product("Курица", cost: 400), k.Product("Салат", cost: 500));
        k.Stock(chicken, 2, 6).Stock(greens, 0.4m, 200);
        // Цезарь закреплён первым, но салата хватит только на 4 порции — это 0,8 кг курицы из двух.
        k.Dish("Цезарь", (chicken, 0.2m), (greens, 0.1m)).HotPinned = true;
        k.Dish("Суп", (chicken, 0.25m));

        var plan = HotOffers.Plan(k.Dishes, k.Batches, k.Totals, Kitchen.Now, new HotRules());

        Assert.Equal([("Цезарь", 4, 0.8m), ("Суп", 5, 1.2m)], plan.Offers.Select(o => (o.DishName, o.Portions, o.Ingredients.Single().UsableQuantity)));
        Assert.Empty(plan.Leftovers);
        Assert.Equal(plan.AtRiskValue, plan.PlannedValue);
    }

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
    public void Менеджер_настраивает_что_важнее_срок_или_сумма_и_правит_список()
    {
        var kitchen = new Kitchen();
        var cheap = kitchen.Product("Зелень", cost: 300);
        var costly = kitchen.Product("Вырезка", cost: 2400);
        // Зелень истекает через 2 часа, вырезка — через 40: по сроку впереди зелень, по сумме — вырезка.
        kitchen.Stock(cheap, 1, expiresInHours: 2).Stock(costly, 1, expiresInHours: 40);
        var salad = kitchen.Dish("Салат", (cheap, 0.2m));
        var steak = kitchen.Dish("Стейк", (costly, 0.2m));

        string[] Order(HotRules rules) => kitchen.HotOffers(rules: rules).Select(o => o.DishName).ToArray();

        Assert.Equal(["Стейк", "Салат"], Order(new HotRules(UrgencyWeight: 0)));
        Assert.Equal(["Салат", "Стейк"], Order(new HotRules(UrgencyWeight: 100)));

        // Закреплённое блюдо идёт первым при любых весах, скрытое не предлагается вовсе.
        salad.HotPinned = true;
        Assert.Equal(["Салат", "Стейк"], Order(new HotRules(UrgencyWeight: 0)));
        (salad.HotPinned, salad.HotHidden) = (false, true);
        Assert.Equal(["Стейк"], Order(new HotRules()));

        // Потолок скидки задаёт менеджер; ноль отключает скидки.
        salad.HotHidden = false;
        Assert.Equal(15, kitchen.HotOffers(rules: new HotRules(MaxDiscountPercent: 20)).Single(o => o.DishName == "Салат").Discount!.Percent);
        Assert.All(kitchen.HotOffers(rules: new HotRules(MaxDiscountPercent: 0)), o => Assert.Null(o.Discount));
    }

    [Fact]
    public void Отчёт_по_поставщикам_делит_привезённое_на_проданное_и_потери_по_причинам()
    {
        var now = Kitchen.Now;
        var fish = new Product { Id = Guid.NewGuid(), Name = "Лосось", Unit = Unit.Kg, ShelfLifeHours = 48, Cost = 1000 };
        var greens = new Product { Id = Guid.NewGuid(), Name = "Салат", Unit = Unit.Kg, ShelfLifeHours = 100, Cost = 200 };
        Supply Supplied(string supplier, double hoursAgo) => new() { Id = Guid.NewGuid(), Number = Guid.NewGuid().ToString("N"), Supplier = supplier, CreatedAt = now.AddHours(-hoursAgo) };
        Batch Lot(Supply supply, Product product, decimal quantity, decimal remaining, double lifeHours) => new()
        {
            Id = Guid.NewGuid(), SupplyId = supply.Id, Supply = supply, ProductId = product.Id, Product = product,
            Quantity = quantity, Remaining = remaining, ExpiresAt = supply.CreatedAt.AddHours(lifeHours), UnitCost = product.Cost,
        };
        var good = Supplied("Северная рыба", 10);
        var bad = Supplied("Овощная база", 60);

        var rows = SupplierReport.Build(
        [
            // Рыба: 10 кг, всё продано или лежит годным, привезена с полным сроком.
            new BatchFlow(Lot(good, fish, 10, 4, 48), Sold: 6, Expired: 0, Defective: 0, Spoiled: 0),
            // Овощи: 10 кг со сроком вдвое короче норматива; 2 кг брак, 1 кг испортился, 1 кг списан по сроку,
            // ещё 2 кг уже просрочены и ждут списания — это тоже потеря.
            new BatchFlow(Lot(bad, greens, 10, 2, 50), Sold: 4, Expired: 1, Defective: 2, Spoiled: 1),
        ], now);

        Assert.Equal(["Овощная база", "Северная рыба"], rows.Select(r => r.Supplier));
        var worst = rows[0];
        Assert.Equal((2000m, 800m, 600m, 400m, 200m, 0m), (worst.Delivered, worst.Sold, worst.Expired, worst.Defective, worst.Spoiled, worst.InStock));
        Assert.Equal((60m, 50, "Салат", SupplierReport.Problem), (worst.LossPercent, worst.ShelfLifePercent, worst.WorstProduct, worst.Verdict));
        var best = rows[1];
        Assert.Equal((10_000m, 6000m, 4000m, 0m, 100, SupplierReport.Reliable), (best.Delivered, best.Sold, best.InStock, best.LossPercent, best.ShelfLifePercent, best.Verdict));
        Assert.Null(best.WorstProduct);
    }

    [Fact]
    public void Прогноз_расхода_берёт_такие_же_дни_недели()
    {
        var product = Guid.NewGuid();
        var today = new DateOnly(2026, 10, 9);   // пятница
        // Две недели: по будням уходит 2 кг, в пятницу и субботу — 6 кг, в воскресенье продаж не было.
        var history = Enumerable.Range(1, 14)
            .Select(i => today.AddDays(-i))
            .Where(d => d.DayOfWeek != DayOfWeek.Sunday)
            .Select(d => new DailyUse(product, d, d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? 6 : 2))
            .ToList();

        // Пятница + суббота + воскресенье: 6 + 6 + 0 кг за 72 часа.
        Assert.Equal(12m / 72, Demand.PerHour(history, today, 3)[product]);
        // Понедельник + вторник + среда: по 2 кг.
        Assert.Equal(6m / 72, Demand.PerHour(history, today.AddDays(3), 3)[product]);
        // Сегодняшние продажи в прогноз не входят: день ещё не закончился.
        Assert.Empty(Demand.PerHour([new DailyUse(product, today, 5)], today, 3));
    }

    [Fact]
    public void Смена_через_полночь_заканчивается_на_следующий_день()
    {
        var zone = TimeSpan.FromHours(3);
        var (start, end) = StopCost.Shift("18:00", "02:00");
        DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, zone);
        double Open(DateTimeOffset from, DateTimeOffset to) => StopCost.OpenHours(from, to, start, end, zone);

        Assert.Equal((TimeSpan.FromHours(18), TimeSpan.FromHours(26)), (start, end));
        Assert.Equal(3, Open(At(7, 23), At(8, 3)));          // до 02:00, дальше ресторан закрыт
        Assert.Equal(1, Open(At(8, 0, 30), At(8, 1, 30)));   // после полуночи — хвост вчерашней смены
        Assert.Equal(0, Open(At(8, 10), At(8, 17)));         // днём закрыто
        Assert.Equal(16, Open(At(7, 12), At(9, 12)));        // двое суток — две смены по 8 часов
        // Обычная смена считается как раньше, «9:00» — то же, что «09:00».
        Assert.Equal((TimeSpan.FromHours(9), TimeSpan.FromHours(24)), StopCost.Shift("9:00", "24:00"));
    }

    [Fact]
    public void Отчёт_смены_берёт_простой_за_день_а_скорость_продаж_за_весь_период()
    {
        var zone = TimeSpan.FromHours(3);
        var (start, end) = StopCost.Shift("12:00", "24:00");
        var now = new DateTimeOffset(2026, 10, 9, 24 - 1, 59, 59, zone).AddSeconds(1);
        var since = now.AddDays(-2);
        var dish = new Dish { Id = Guid.NewGuid(), Name = "Стейк", Price = 1000 };
        // Вчера блюдо стояло 6 часов, сегодня — 3.
        (DateTimeOffset, DateTimeOffset)[] stops =
        [
            (new DateTimeOffset(2026, 10, 8, 12, 0, 0, zone), new DateTimeOffset(2026, 10, 8, 18, 0, 0, zone)),
            (new DateTimeOffset(2026, 10, 9, 20, 0, 0, zone), new DateTimeOffset(2026, 10, 9, 23, 0, 0, zone)),
        ];

        var row = StopCost.Of(dish, stops, soldPortions: 30, since, now, start, end, zone, from: now.AddDays(-1));

        // За два дня блюдо продавалось 24 − 9 = 15 часов: 30 порций ÷ 15 ч = 2 в час; за сегодняшние 3 часа стопа — 6 порций.
        Assert.Equal((3, 2, 6, 6000), (row.StopHours, row.PortionsPerHour, row.LostPortions, row.LostRevenue));
    }

    [Fact]
    public void Цена_стопа_считает_только_часы_смены()
    {
        var zone = TimeSpan.FromHours(3);
        var (start, end) = (StopCost.ParseTime("12:00"), StopCost.ParseTime("24:00"));
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, zone);
        var since = now.AddDays(-2);
        var dish = new Dish { Id = Guid.NewGuid(), Name = "Стейк", Price = 1000 };
        // Стоп с 20:00 позавчера до 14:00 вчера: 4 часа вечером и 2 часа после открытия, ночь не считается.
        var stop = (new DateTimeOffset(2026, 10, 7, 20, 0, 0, zone), new DateTimeOffset(2026, 10, 8, 14, 0, 0, zone));

        var row = StopCost.Of(dish, [stop], soldPortions: 36, since, now, start, end, zone);

        Assert.Equal(6, row.StopHours);
        // За два дня смена длилась 24 часа, 18 из них блюдо продавалось: 36 порций ÷ 18 ч = 2 в час.
        Assert.Equal(2, row.PortionsPerHour);
        Assert.Equal(12, row.LostPortions);
        Assert.Equal(12_000, row.LostRevenue);
    }

    [Fact]
    public void Скидка_не_опускает_цену_ниже_стоимости_неистекающих_продуктов()
    {
        var kitchen = new Kitchen();
        var salmon = kitchen.Product("Лосось", cost: 1900);
        var shrimp = kitchen.Product("Креветки", cost: 1900);
        var beef = kitchen.Product("Вырезка", cost: 2400);
        kitchen.Stock(salmon, 2, expiresInHours: 4).Stock(shrimp, 2, expiresInHours: 4).Stock(beef, 5, expiresInHours: 200);
        kitchen.Dish("Стейк из лосося", (salmon, 0.2m));
        // В этом блюде вырезка стоит 432 ₽ при цене 500 ₽: скидка больше 13% съела бы её стоимость.
        kitchen.Dish("Сёрф-энд-тёрф", (shrimp, 0.1m), (beef, 0.18m));

        var offers = kitchen.HotOffers();
        var steak = offers.Single(o => o.DishName == "Стейк из лосося").Discount!;
        var surf = offers.Single(o => o.DishName == "Сёрф-энд-тёрф").Discount!;

        // Срочность 1 − 4/48, спроса нет: 40% × 0,92 = 36,7% → 35%.
        Assert.Equal(35, steak.Percent);
        Assert.Equal(325, steak.Price);
        Assert.Equal(10, surf.Percent);
        Assert.True(surf.Price >= surf.FloorPrice);

        // Если партия и так продастся до конца срока, скидка не нужна.
        var selling = kitchen.HotOffers(perHour: new() { [salmon.Id] = 1, [shrimp.Id] = 1 });
        Assert.All(selling, o => Assert.Null(o.Discount));
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
