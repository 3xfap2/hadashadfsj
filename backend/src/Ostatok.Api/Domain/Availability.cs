namespace Ostatok.Api.Domain;

public enum DishStatus { Available, Low, Stop }

public enum StopReason
{
    /// <summary>Продукт есть, но весь просрочен.</summary>
    Expired,
    /// <summary>Продукта нет на складе.</summary>
    OutOfStock,
    /// <summary>Годный остаток есть, но меньше одной порции.</summary>
    NotEnough,
}

public sealed record DishAvailability(
    DishStatus Status,
    int Portions,
    Guid LimitingProductId,
    StopReason? Reason);

/// <summary>Годные остатки по продуктам и продукты, у которых лежит просрочка.</summary>
public sealed record StockTotals(
    IReadOnlyDictionary<Guid, decimal> Fresh,
    IReadOnlySet<Guid> WithExpired);

public static class Availability
{
    public const int LowPortions = 5;

    /// <summary>
    /// Сколько порций блюда можно приготовить из годных остатков и какой продукт закончится первым.
    /// Ноль порций — блюдо в стоп-листе.
    /// </summary>
    public static DishAvailability Of(Dish dish, StockTotals stock)
    {
        var portions = int.MaxValue;
        var limiting = Guid.Empty;
        foreach (var item in dish.Recipe)
        {
            var possible = (int)Math.Min(int.MaxValue, Math.Floor(Fresh(stock, item.ProductId) / item.Quantity));
            if (possible < portions)
            {
                portions = possible;
                limiting = item.ProductId;
            }
        }

        if (portions > 0)
        {
            var status = portions <= LowPortions ? DishStatus.Low : DishStatus.Available;
            return new DishAvailability(status, portions, limiting, null);
        }

        var reason = Fresh(stock, limiting) > 0 ? StopReason.NotEnough
            : stock.WithExpired.Contains(limiting) ? StopReason.Expired
            : StopReason.OutOfStock;
        return new DishAvailability(DishStatus.Stop, 0, limiting, reason);
    }

    private static decimal Fresh(StockTotals stock, Guid productId) =>
        stock.Fresh.GetValueOrDefault(productId);
}
