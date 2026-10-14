namespace Ostatok.Api.Domain;

/// <summary>Основной продукт поставщика и его средняя закупочная цена за единицу.</summary>
public sealed record SupplierProduct(string Name, Unit Unit, decimal Quantity, decimal AveragePrice);

/// <param name="Restaurants">У скольких ресторанов владельца этот поставщик был.</param>
/// <param name="DefectivePercent">Брак поставки — в процентах от поставленного (в рублях).</param>
/// <param name="SpoiledPercent">Испортилось раньше срока — в процентах от поставленного.</param>
/// <param name="ExpiredPercent">Не успели продать до конца срока — в процентах от поставленного.</param>
/// <param name="Score">Оценка от 0 до 100, см. <see cref="SupplierRating.Formula"/>.</param>
/// <param name="Products">До трёх продуктов, которых у поставщика куплено больше всего (в рублях).</param>
public sealed record SupplierRatingRow(
    int Place, string Supplier, int Restaurants, int Supplies, decimal Delivered,
    decimal DefectivePercent, decimal SpoiledPercent, decimal ExpiredPercent, decimal LossPercent,
    int ShelfLifePercent, int Score, string Verdict, IEnumerable<SupplierProduct> Products);

/// <summary>
/// Рейтинг поставщиков по всем ресторанам владельца. Строится на отчёте по поставщикам (<see cref="SupplierReport"/>):
/// те же суммы в рублях по закупочной цене, к ним — доли потерь по причинам, средние цены и итоговая оценка.
/// </summary>
public static class SupplierRating
{
    public const string Formula =
        "Оценка = 100 − 3 × доля брака − 2 × доля порчи раньше срока − 1 × доля просрочки (доли — в % от поставленного в рублях). " +
        "Брак и порча — вина поставщика, поэтому весят больше; просрочка больше зависит от того, сколько заказал ресторан. " +
        "При равной оценке выше тот, у кого поставлено больше.";

    public static int Score(decimal defective, decimal spoiled, decimal expired) =>
        (int)Math.Clamp(Math.Round(100 - 3 * defective - 2 * spoiled - expired), 0, 100);

    public static IReadOnlyList<SupplierRatingRow> Build(IReadOnlyCollection<BatchFlow> flows, DateTimeOffset now)
    {
        var groups = flows.ToLookup(f => string.IsNullOrWhiteSpace(f.Batch.Supply!.Supplier) ? SupplierReport.NoName : f.Batch.Supply.Supplier!);
        decimal Percent(decimal part, decimal whole) => whole > 0 ? Math.Round(part / whole * 100, 1) : 0;

        return SupplierReport.Build(flows, now)
            .Select(r =>
            {
                var group = groups[r.Supplier];
                var (defective, spoiled, expired) = (Percent(r.Defective, r.Delivered), Percent(r.Spoiled, r.Delivered), Percent(r.Expired, r.Delivered));
                var products = group
                    .GroupBy(f => (f.Batch.Product!.Name, f.Batch.Product.Unit))
                    .Select(p => (p.Key, Quantity: p.Sum(f => f.Batch.Quantity), Value: p.Sum(f => f.Batch.Quantity * f.Batch.UnitCost)))
                    .OrderByDescending(p => p.Value).ThenBy(p => p.Key.Name)
                    .Take(3)
                    .Select(p => new SupplierProduct(p.Key.Name, p.Key.Unit, p.Quantity, Math.Round(p.Value / p.Quantity, 2)))
                    .ToList();
                return (Row: r, Restaurants: group.Select(f => f.Batch.RestaurantId).Distinct().Count(),
                    defective, spoiled, expired, Score: Score(defective, spoiled, expired), products);
            })
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Row.Delivered).ThenBy(x => x.Row.Supplier)
            .Select((x, i) => new SupplierRatingRow(
                i + 1, x.Row.Supplier, x.Restaurants, x.Row.Supplies, x.Row.Delivered,
                x.defective, x.spoiled, x.expired, x.Row.LossPercent, x.Row.ShelfLifePercent, x.Score, x.Row.Verdict, x.products))
            .ToList();
    }
}
