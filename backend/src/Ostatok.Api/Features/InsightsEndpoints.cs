using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record WriteOffDay(DateOnly Date, decimal Value);
public sealed record WriteOffProduct(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Value);
public sealed record WriteOffReason(string Reason, decimal Value);
public sealed record WriteOffReport(
    int Days, decimal Total,
    IEnumerable<WriteOffDay> ByDay, IEnumerable<WriteOffProduct> ByProduct, IEnumerable<WriteOffReason> ByReason);

public sealed record DeficitRow(Guid ProductId, string ProductName, Unit Unit, int RejectedOrders, decimal MissingQuantity);

public static class InsightsEndpoints
{
    public static void MapInsights(this RouteGroupBuilder api)
    {
        api.MapGet("/hot-offers", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            return HotOffers.Rank(
                await db.LoadDishesAsync(ct),
                await db.LoadFreshBatchesAsync(now, ct),
                await db.LoadTotalsAsync(now, ct),
                now, options.HotOfferHours);
        })
        .WithTags("Горячие предложения");

        api.MapGet("/purchase-suggestions", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            return Forecast.Purchases(
                await db.Products.AsNoTracking().ToListAsync(ct),
                await db.LoadDishesAsync(ct),
                (await db.LoadFreshBatchesAsync(now, ct)).ToLookup(b => b.ProductId),
                await db.LoadConsumptionPerHourAsync(now, options.ConsumptionDays, ct),
                now, options.PurchaseCoverHours);
        })
        .WithTags("Закупки");

        var analytics = api.MapGroup("/analytics").WithTags("Аналитика");

        analytics.MapGet("/write-offs", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct, int days = 14) =>
        {
            days = Math.Clamp(days, 1, 90);
            var offset = TimeSpan.FromHours(options.UtcOffsetHours);
            var today = DateOnly.FromDateTime(time.GetUtcNow().ToOffset(offset).Date);
            var from = today.AddDays(1 - days);
            var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset).ToUniversalTime();

            var rows = await db.Movements
                .Where(m => m.Type == MovementType.WriteOff && m.At >= since)
                .Select(m => new { m.At, m.Quantity, m.Note, m.ProductId, m.Product!.Name, m.Product.Unit, m.Product.Cost })
                .ToListAsync(ct);

            var byDay = rows.ToLookup(r => DateOnly.FromDateTime(r.At.ToOffset(offset).Date));
            return new WriteOffReport(
                days,
                rows.Sum(r => r.Quantity * r.Cost),
                // Дни без списаний остаются в ряду нулями — график не «склеивает» даты.
                Enumerable.Range(0, days).Select(i => from.AddDays(i))
                    .Select(d => new WriteOffDay(d, byDay[d].Sum(r => r.Quantity * r.Cost))),
                rows.GroupBy(r => new { r.ProductId, r.Name, r.Unit })
                    .Select(g => new WriteOffProduct(g.Key.ProductId, g.Key.Name, g.Key.Unit, g.Sum(r => r.Quantity), g.Sum(r => r.Quantity * r.Cost)))
                    .OrderByDescending(p => p.Value),
                rows.GroupBy(r => r.Note ?? "Без причины")
                    .Select(g => new WriteOffReason(g.Key, g.Sum(r => r.Quantity * r.Cost)))
                    .OrderByDescending(r => r.Value));
        });

        analytics.MapGet("/deficit", async (AppDbContext db, TimeProvider time, CancellationToken ct, int days = 14) =>
        {
            var since = time.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90));
            var rows = await db.Orders
                .Where(o => o.Status == OrderStatus.Rejected && o.CreatedAt >= since)
                .SelectMany(o => o.Shortages)
                .GroupBy(s => new { s.ProductId, s.Product!.Name, s.Product.Unit })
                .Select(g => new DeficitRow(g.Key.ProductId, g.Key.Name, g.Key.Unit, g.Count(), g.Sum(s => s.Required - s.Available)))
                .ToListAsync(ct);
            return rows.OrderByDescending(r => r.RejectedOrders).ThenByDescending(r => r.MissingQuantity);
        });
    }
}
