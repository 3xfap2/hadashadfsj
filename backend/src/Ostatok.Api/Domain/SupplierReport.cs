namespace Ostatok.Api.Domain;

/// <summary>Что стало с одной партией: сколько продано и сколько списано по каждой причине.</summary>
public sealed record BatchFlow(Batch Batch, decimal Sold, decimal Expired, decimal Defective, decimal Spoiled);

/// <param name="Delivered">Стоимость всего привезённого по закупочным ценам.</param>
/// <param name="Expired">Списано по истечении срока (включая то, что уже просрочено и ждёт списания).</param>
/// <param name="Defective">Списано как брак поставки.</param>
/// <param name="Spoiled">Списано как порча при хранении.</param>
/// <param name="InStock">Годный остаток на складе.</param>
/// <param name="LossPercent">Доля привезённого, которая ушла в списание.</param>
/// <param name="ShelfLifePercent">
/// С каким запасом срока поставщик привозит товар: средний срок партии при приёмке в процентах от норматива продукта.
/// </param>
/// <param name="WorstProduct">Продукт с наибольшими потерями у этого поставщика.</param>
public sealed record SupplierRow(
    string Supplier, int Supplies, int Batches,
    decimal Delivered, decimal Sold, decimal Expired, decimal Defective, decimal Spoiled, decimal InStock,
    decimal LossPercent, int ShelfLifePercent, string? WorstProduct, string Verdict);

/// <summary>
/// Отчёт по поставщикам: у кого привезённое продаётся, а у кого уходит в списание и почему.
/// Всё считается в рублях по закупочной цене, чтобы килограммы и штуки можно было сложить.
/// </summary>
public static class SupplierReport
{
    public const string Reliable = "Надёжный";
    public const string HasLosses = "Есть потери";
    public const string Problem = "Проблемный";
    public const string NoName = "Без поставщика";

    public static IReadOnlyList<SupplierRow> Build(IReadOnlyCollection<BatchFlow> flows, DateTimeOffset now) =>
        flows
            .GroupBy(f => string.IsNullOrWhiteSpace(f.Batch.Supply!.Supplier) ? NoName : f.Batch.Supply.Supplier!)
            .Select(group =>
            {
                decimal Value(Func<BatchFlow, decimal> quantity) => group.Sum(f => quantity(f) * f.Batch.UnitCost);

                var delivered = Value(f => f.Batch.Quantity);
                // Просроченный остаток уже потерян, даже если его ещё не списали.
                var expired = Value(f => f.Expired + (f.Batch.ExpiresAt <= now ? f.Batch.Remaining : 0));
                var defective = Value(f => f.Defective);
                var spoiled = Value(f => f.Spoiled);
                var loss = delivered > 0 ? Math.Round((expired + defective + spoiled) / delivered * 100, 1) : 0;

                var worst = group
                    .GroupBy(f => f.Batch.Product!.Name)
                    .Select(p => (Name: p.Key, Loss: p.Sum(f => (f.Expired + f.Defective + f.Spoiled + (f.Batch.ExpiresAt <= now ? f.Batch.Remaining : 0)) * f.Batch.UnitCost)))
                    .Where(p => p.Loss > 0)
                    .OrderByDescending(p => p.Loss)
                    .Select(p => p.Name)
                    .FirstOrDefault();

                return new SupplierRow(
                    group.Key,
                    group.Select(f => f.Batch.SupplyId).Distinct().Count(),
                    group.Count(),
                    Math.Round(delivered), Math.Round(Value(f => f.Sold)), Math.Round(expired), Math.Round(defective), Math.Round(spoiled),
                    Math.Round(Value(f => f.Batch.ExpiresAt > now ? f.Batch.Remaining : 0)),
                    loss,
                    (int)Math.Round(group.Average(f => Math.Min(1.0, (f.Batch.ExpiresAt - f.Batch.Supply!.CreatedAt).TotalHours / f.Batch.Product!.ShelfLifeHours)) * 100),
                    worst,
                    loss < 3 ? Reliable : loss < 10 ? HasLosses : Problem);
            })
            .OrderByDescending(r => r.LossPercent).ThenByDescending(r => r.Delivered)
            .ToList();
}
