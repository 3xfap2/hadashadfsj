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

/// <param name="StopHours">Сколько часов блюда простояли в стопе из-за этого продукта.</param>
/// <param name="Dishes">Блюда, которые из-за него стояли.</param>
public sealed record DeficitRow(
    Guid ProductId, string ProductName, Unit Unit, int RejectedOrders, decimal MissingQuantity, double StopHours, IEnumerable<string> Dishes);

public sealed record StopCostReport(int Days, decimal Total, IEnumerable<StopCostRow> Dishes);

public sealed record AnalyticsSummary(int Orders, int RejectedOrders, double StopHours);

/// <param name="Supplier">Поставщик из последней поставки этого продукта.</param>
/// <param name="Sum">Стоимость предлагаемого количества по цене из справочника.</param>
public sealed record PurchaseRow(
    Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Fresh, decimal PerDay, double? HoursCovered, string Reason,
    string? Supplier, decimal Sum);

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
                now, await db.LoadHotRulesAsync(options, ct),
                await db.LoadConsumptionPerHourAsync(now, options, ct));
        })
        .WithTags("Горячие предложения");

        api.MapGet("/purchase-suggestions", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var products = await db.Products.AsNoTracking().Include(p => p.PrepRecipe).ToListAsync(ct);
            var made = products.Where(p => p.PrepRecipe.Count > 0).ToList();
            // Заготовки не закупают, а готовят (см. /api/preps). Зато их состав — такой же потребитель сырья,
            // как техкарта блюда, поэтому в расчёт закупок заготовка входит как «блюдо».
            var users = (await db.LoadDishesAsync(ct))
                .Concat(made.Select(p => new Dish
                {
                    Id = p.Id, Name = $"заготовка «{p.Name}»",
                    Recipe = p.PrepRecipe.Select(i => new RecipeItem { ProductId = i.ProductId, Quantity = i.Quantity }).ToList(),
                }))
                .ToList();
            var suggestions = Forecast.Purchases(
                products.Except(made).ToList(),
                users,
                (await db.LoadFreshBatchesAsync(now, ct)).ToLookup(b => b.ProductId),
                await db.LoadConsumptionPerHourAsync(now, options, ct),
                now, options.PurchaseCoverHours);

            // У кого этот продукт брали в последний раз — к тому и заявка.
            var suppliers = (await db.Batches
                    .Where(b => b.Supply!.Supplier != null)
                    .Select(b => new { b.ProductId, b.Supply!.Supplier, b.Supply.CreatedAt })
                    .ToListAsync(ct))
                .GroupBy(b => b.ProductId)
                .ToDictionary(g => g.Key, g => g.MaxBy(b => b.CreatedAt)!.Supplier);
            var cost = products.ToDictionary(p => p.Id, p => p.Cost);
            return suggestions.Select(s => new PurchaseRow(
                s.ProductId, s.ProductName, s.Unit, s.Quantity, s.Fresh, s.PerDay, s.HoursCovered, s.Reason,
                suppliers.GetValueOrDefault(s.ProductId), s.Quantity * cost[s.ProductId]));
        })
        .WithTags("Закупки")
        .RequireRoles(Access.Purchasing);

        var analytics = api.MapGroup("/analytics").WithTags("Аналитика").RequireRoles(Access.Analytics);

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
            var now = time.GetUtcNow();
            var since = now.AddDays(-Math.Clamp(days, 1, 90));
            var rejected = (await db.Orders
                    .Where(o => o.Status == OrderStatus.Rejected && o.CreatedAt >= since)
                    .SelectMany(o => o.Shortages)
                    .GroupBy(s => s.ProductId)
                    .Select(g => new { ProductId = g.Key, Count = g.Count(), Missing = g.Sum(s => s.Required - s.Available) })
                    .ToListAsync(ct))
                .ToDictionary(r => r.ProductId);
            var stops = (await db.StopLogs.AsNoTracking().Include(l => l.Dish)
                    .Where(l => l.EndedAt == null || l.EndedAt >= since)
                    .ToListAsync(ct))
                .ToLookup(l => l.ProductId);
            var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);

            return rejected.Keys.Union(stops.Select(g => g.Key))
                .Where(products.ContainsKey)
                .Select(id => new DeficitRow(
                    id, products[id].Name, products[id].Unit,
                    rejected.GetValueOrDefault(id)?.Count ?? 0,
                    rejected.GetValueOrDefault(id)?.Missing ?? 0,
                    Math.Round(stops[id].Sum(l => StopTracker.Hours(l, since, now)), 1),
                    stops[id].Select(l => l.Dish!.Name).Distinct().Order()))
                .OrderByDescending(r => r.StopHours).ThenByDescending(r => r.RejectedOrders).ThenBy(r => r.ProductName);
        });

        analytics.MapGet("/stop-cost", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct, int days = 14) =>
        {
            days = Math.Clamp(days, 1, 90);
            var now = time.GetUtcNow();
            var since = now.AddDays(-days);
            var stops = (await db.StopLogs.AsNoTracking().Where(l => l.EndedAt == null || l.EndedAt >= since).ToListAsync(ct)).ToLookup(l => l.DishId);
            var sold = await db.Orders
                .Where(o => o.Status == OrderStatus.Accepted && o.CreatedAt >= since)
                .SelectMany(o => o.Items)
                .GroupBy(i => i.DishId)
                .Select(g => new { DishId = g.Key, Portions = g.Sum(i => i.Quantity) })
                .ToDictionaryAsync(g => g.DishId, g => g.Portions, ct);
            var restaurant = await db.CurrentRestaurantAsync(ct);
            var (start, end, zone) = (StopCost.ParseTime(restaurant.ShiftStart), StopCost.ParseTime(restaurant.ShiftEnd), TimeSpan.FromHours(options.UtcOffsetHours));

            var rows = (await db.Dishes.AsNoTracking().ToListAsync(ct))
                .Where(d => stops.Contains(d.Id))
                .Select(d => StopCost.Of(d, stops[d.Id].Select(l => (l.StartedAt, l.EndedAt ?? now)), sold.GetValueOrDefault(d.Id), since, now, start, end, zone))
                .Where(r => r.StopHours > 0)
                .OrderByDescending(r => r.LostRevenue).ThenByDescending(r => r.StopHours)
                .ToList();
            return new StopCostReport(days, rows.Sum(r => r.LostRevenue), rows);
        });

        analytics.MapGet("/summary", async (AppDbContext db, TimeProvider time, CancellationToken ct, int days = 14) =>
        {
            var now = time.GetUtcNow();
            var since = now.AddDays(-Math.Clamp(days, 1, 90));
            var orders = await db.Orders.Where(o => o.CreatedAt >= since).GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var logs = await db.StopLogs.AsNoTracking().Where(l => l.EndedAt == null || l.EndedAt >= since).ToListAsync(ct);
            return new AnalyticsSummary(
                orders.Sum(o => o.Count),
                orders.Where(o => o.Key == OrderStatus.Rejected).Sum(o => o.Count),
                Math.Round(logs.Sum(l => StopTracker.Hours(l, since, now)), 1));
        });
    }
}
