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

public sealed record SupplierReportResponse(int Days, decimal Delivered, decimal Lost, IEnumerable<SupplierRow> Suppliers);

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

        // Отчёт по поставщикам: что стало с привезённым за период — продано, списано (и почему), лежит на складе.
        analytics.MapGet("/suppliers", async (AppDbContext db, TimeProvider time, CancellationToken ct, int days = 30) =>
        {
            days = Math.Clamp(days, 1, 90);
            var now = time.GetUtcNow();
            var since = now.AddDays(-days);
            // Партии, которые система создаёт сама (заготовки, проверка сроков, генератор), — не от поставщиков.
            string[] own = [PrepService.Workshop, SimulatorEndpoints.CheckSupplier, Simulator.Supplier];
            var batches = await db.Batches.AsNoTracking()
                .Include(b => b.Product).Include(b => b.Supply)
                .Where(b => b.Supply!.CreatedAt >= since && (b.Supply.Supplier == null || !own.Contains(b.Supply.Supplier)))
                .ToListAsync(ct);
            var moves = (await (
                    from m in db.Movements
                    join b in db.Batches on m.BatchId equals b.Id
                    where b.Supply!.CreatedAt >= since && m.Type != MovementType.Supply && m.Type != MovementType.PrepMade
                    group m by new { m.BatchId, m.Type, m.Note } into g
                    select new { g.Key.BatchId, g.Key.Type, g.Key.Note, Quantity = g.Sum(m => m.Quantity) }).ToListAsync(ct))
                .ToLookup(m => m.BatchId);

            var flows = batches.Select(b =>
            {
                decimal Sum(Func<MovementType, string?, bool> match) => moves[b.Id].Where(m => match(m.Type, m.Note)).Sum(m => m.Quantity);
                bool WriteOff(MovementType type) => type == MovementType.WriteOff;
                return new BatchFlow(b,
                    Sold: Sum((type, _) => type is MovementType.Order or MovementType.PrepUse),
                    Defective: Sum((type, note) => WriteOff(type) && note == WriteOffService.DefectiveNote),
                    Spoiled: Sum((type, note) => WriteOff(type) && note == WriteOffService.SpoiledNote),
                    // Всё остальное списание — по сроку: вручную или автоматически.
                    Expired: Sum((type, note) => WriteOff(type) && note != WriteOffService.DefectiveNote && note != WriteOffService.SpoiledNote));
            }).ToList();

            var rows = SupplierReport.Build(flows, now);
            return new SupplierReportResponse(days, rows.Sum(r => r.Delivered), rows.Sum(r => r.Expired + r.Defective + r.Spoiled), rows);
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
