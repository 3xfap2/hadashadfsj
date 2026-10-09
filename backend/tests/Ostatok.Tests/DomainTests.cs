using Ostatok.Api.Domain;

namespace Ostatok.Tests;

public class FefoTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Salmon = Guid.NewGuid();
    private static readonly Guid Cream = Guid.NewGuid();

    private static Batch Batch(Guid productId, decimal remaining, double expiresInHours) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = productId,
        Quantity = remaining,
        Remaining = remaining,
        ExpiresAt = Now.AddHours(expiresInHours),
    };

    [Fact]
    public void Берёт_партию_с_ближайшим_сроком()
    {
        var late = Batch(Salmon, 1, 40);
        var early = Batch(Salmon, 1, 5);

        var result = Fefo.Allocate([late, early], new Dictionary<Guid, decimal> { [Salmon] = 0.2m }, Now);

        Assert.Equal([new Allocation(early.Id, Salmon, 0.2m)], result.Allocations);
    }

    [Fact]
    public void Добирает_из_следующей_партии_когда_ближайшей_не_хватает()
    {
        var early = Batch(Salmon, 0.15m, 5);
        var late = Batch(Salmon, 1, 40);

        var result = Fefo.Allocate([late, early], new Dictionary<Guid, decimal> { [Salmon] = 0.2m }, Now);

        Assert.Equal(
            [new Allocation(early.Id, Salmon, 0.15m), new Allocation(late.Id, Salmon, 0.05m)],
            result.Allocations);
    }

    [Fact]
    public void Просроченная_партия_не_используется()
    {
        var expired = Batch(Salmon, 5, -1);
        var fresh = Batch(Salmon, 0.1m, 20);

        var result = Fefo.Allocate([expired, fresh], new Dictionary<Guid, decimal> { [Salmon] = 0.2m }, Now);

        Assert.False(result.Ok);
        Assert.Equal([new Shortage(Salmon, 0.2m, 0.1m)], result.Shortages);
    }

    [Fact]
    public void Партия_с_истёкшим_ровно_сейчас_сроком_уже_не_годна()
    {
        var result = Fefo.Allocate([Batch(Salmon, 5, 0)], new Dictionary<Guid, decimal> { [Salmon] = 1 }, Now);

        Assert.False(result.Ok);
    }

    [Fact]
    public void При_нехватке_одного_продукта_не_списывается_ничего()
    {
        var required = new Dictionary<Guid, decimal> { [Salmon] = 0.2m, [Cream] = 0.5m };

        var result = Fefo.Allocate([Batch(Salmon, 1, 5), Batch(Cream, 0.1m, 50)], required, Now);

        Assert.Empty(result.Allocations);
        Assert.Equal([new Shortage(Cream, 0.5m, 0.1m)], result.Shortages);
    }
}

public class AvailabilityTests
{
    private static readonly Guid Salmon = Guid.NewGuid();
    private static readonly Guid Cream = Guid.NewGuid();

    private static readonly Dish Steak = new()
    {
        Name = "Стейк из лосося",
        Recipe =
        [
            new RecipeItem { ProductId = Salmon, Quantity = 0.2m },
            new RecipeItem { ProductId = Cream, Quantity = 0.03m },
        ],
    };

    private static StockTotals Stock(decimal salmon, decimal cream, params Guid[] withExpired) =>
        new(new Dictionary<Guid, decimal> { [Salmon] = salmon, [Cream] = cream }, withExpired.ToHashSet());

    [Fact]
    public void Порции_считаются_по_самому_дефицитному_продукту()
    {
        var a = Availability.Of(Steak, Stock(salmon: 5, cream: 0.1m));

        Assert.Equal(new DishAvailability(DishStatus.Low, 3, Cream, null), a);
    }

    [Fact]
    public void Запаса_больше_порога_блюдо_в_продаже()
    {
        Assert.Equal(DishStatus.Available, Availability.Of(Steak, Stock(5, 5)).Status);
    }

    [Fact]
    public void Стоп_когда_продукта_нет()
    {
        var a = Availability.Of(Steak, Stock(0, 5));

        Assert.Equal(new DishAvailability(DishStatus.Stop, 0, Salmon, StopReason.OutOfStock), a);
    }

    [Fact]
    public void Стоп_с_причиной_просрочка_когда_остался_только_просроченный_продукт()
    {
        Assert.Equal(StopReason.Expired, Availability.Of(Steak, Stock(0, 5, Salmon)).Reason);
    }

    [Fact]
    public void Стоп_когда_остатка_меньше_одной_порции()
    {
        Assert.Equal(StopReason.NotEnough, Availability.Of(Steak, Stock(0.15m, 5)).Reason);
    }
}
