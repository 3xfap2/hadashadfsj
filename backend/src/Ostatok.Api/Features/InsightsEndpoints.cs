using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record WriteOffDay(DateOnly Date, decimal Value);
public sealed record WriteOffProduct(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Value);
public sealed record WriteOffReason(string Reason, decimal Value);
/// <param name="Total">Все потери за период: списания и недостача по инвентаризации.</param>
/// <param name="Surplus">Излишек, найденный при инвентаризации; потери он не уменьшает и показывается отдельно.</param>
public sealed record WriteOffReport(
    int Days, decimal Total,
    IEnumerable<WriteOffDay> ByDay, IEnumerable<WriteOffProduct> ByProduct, IEnumerable<WriteOffReason> ByReason, decimal Surplus);

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

public sealed record ShiftDish(Guid DishId, string DishName, int Portions, decimal Revenue);
public sealed record ShiftExpiring(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, DateTimeOffset ExpiresAt, decimal Value);

/// <summary>Отчёт за смену: что продали, что потеряли и что сделать к завтрашнему дню.</summary>
/// <param name="From">Начало и конец суток отчёта; при смене через полночь сутки кончаются вместе со сменой.</param>
/// <param name="Losses">Списания и недостача по инвентаризации за смену, ₽; по причинам — в WriteOffs.</param>
/// <param name="StopCost">Сколько выручки недополучено из-за стопов за смену.</param>
/// <param name="Expiring">Что истечёт до конца завтрашнего дня. Заполнено только в отчёте за текущую смену, как и ToOrder: это состояние склада сейчас.</param>
public sealed record ShiftReport(
    DateOnly Date, DateTimeOffset From, DateTimeOffset To, bool Current,
    int Orders, int RejectedOrders, decimal Revenue, IEnumerable<ShiftDish> TopDishes,
    decimal Losses, IEnumerable<WriteOffReason> WriteOffs, decimal Surplus,
    decimal StopCost, IEnumerable<StopCostRow> Stops,
    IEnumerable<ShiftExpiring>? Expiring, IEnumerable<PurchaseRow>? ToOrder);

public static class InsightsEndpoints
{
    public const string ShortageReason = "Недостача по инвентаризации";

    public static void MapInsights(this RouteGroupBuilder api)
    {
        api.MapGet("/hot-offers", async (AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            return HotOffers.Rank(
                await db.LoadDishesAsync(ct),
                await db.LoadFreshBatchesAsync(now, ct),
                await db.LoadTotalsAsync(now, ct),
                now, await db.LoadHotRulesAsync(options, ct),
                await db.LoadConsumptionPerHourAsync(now, options, ct),
                user.SeesCosts());
        })
        .WithTags("Горячие предложения");

        api.MapGet("/purchase-suggestions", (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct) =>
            PurchasesAsync(db, time.GetUtcNow(), options, ct))
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

            var (rows, surplus) = await LossesAsync(db, since, null, ct);
            var byDay = rows.ToLookup(r => DateOnly.FromDateTime(r.At.ToOffset(offset).Date));
            return new WriteOffReport(
                days,
                rows.Sum(r => r.Value),
                // Дни без списаний остаются в ряду нулями — график не «склеивает» даты.
                Enumerable.Range(0, days).Select(i => from.AddDays(i))
                    .Select(d => new WriteOffDay(d, byDay[d].Sum(r => r.Value))),
                rows.GroupBy(r => new { r.ProductId, r.Name, r.Unit })
                    .Select(g => new WriteOffProduct(g.Key.ProductId, g.Key.Name, g.Key.Unit, g.Sum(r => r.Quantity), g.Sum(r => r.Value)))
                    .OrderByDescending(p => p.Value),
                ByReason(rows),
                surplus);
        });

        // Отчёт за смену — на одну страницу: продажи, потери, стопы и что сделать к завтра. Считается теми же
        // расчётами, что и остальная аналитика, только за одни сутки ресторана.
        analytics.MapGet("/shift", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct, DateOnly? date = null) =>
        {
            var now = time.GetUtcNow();
            if (date is { Year: < 2000 or > 2100 })
                return new Errors().Add("date", "Дата отчёта — от 2000 до 2100 года.").ToResult();
            var zone = TimeSpan.FromHours(options.UtcOffsetHours);
            var restaurant = await db.CurrentRestaurantAsync(ct);
            // Смена через полночь принадлежит дню, в который началась: сутки отчёта кончаются вместе с ней.
            var end = StopCost.Shift(restaurant.ShiftStart, restaurant.ShiftEnd).End;
            var cut = end > TimeSpan.FromDays(1) ? end - TimeSpan.FromDays(1) : TimeSpan.Zero;
            var day = date ?? DateOnly.FromDateTime((now.ToOffset(zone) - cut).Date);
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue) + cut, zone).ToUniversalTime();
            var to = from.AddDays(1);
            var current = from <= now && now < to;

            var orders = await db.Orders.AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Dish)
                .Where(o => o.CreatedAt >= from && o.CreatedAt < to)
                .ToListAsync(ct);
            var accepted = orders.Where(o => o.Status == OrderStatus.Accepted).ToList();
            var (losses, surplus) = await LossesAsync(db, from, to, ct);
            // Скорость продаж блюда берётся за две недели до конца смены: по одному дню её не оценить.
            var until = to < now ? to : now;
            var stops = from > now ? [] : await StopCostAsync(db, options, until.AddDays(-14), until, ct, from);

            return Results.Ok(new ShiftReport(
                day, from, to, current,
                orders.Count, orders.Count - accepted.Count, accepted.Sum(OrderEndpoints.Total),
                accepted.SelectMany(o => o.Items).GroupBy(i => i.DishId)
                    .Select(g => new ShiftDish(g.Key, g.First().Dish!.Name, g.Sum(i => i.Quantity), g.Sum(OrderEndpoints.LineTotal)))
                    .OrderByDescending(d => d.Revenue).ThenBy(d => d.DishName)
                    .Take(5),
                losses.Sum(r => r.Value), ByReason(losses), surplus,
                stops.Sum(r => r.LostRevenue), stops,
                !current ? null : (await db.LoadFreshBatchesAsync(now, ct))
                    .Where(b => b.ExpiresAt <= to.AddDays(1))
                    .GroupBy(b => b.ProductId)
                    .Select(g => new ShiftExpiring(g.Key, g.First().Product!.Name, g.First().Product!.Unit, g.Sum(b => b.Remaining), g.Min(b => b.ExpiresAt), g.Sum(b => b.Remaining * b.UnitCost)))
                    .OrderBy(e => e.ExpiresAt),
                !current ? null : await PurchasesAsync(db, now, options, ct)));
        }).Produces<ShiftReport>();

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
            var rows = await StopCostAsync(db, options, now.AddDays(-days), now, ct);
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
                    where b.Supply!.CreatedAt >= since && m.Type != MovementType.Supply && m.Type != MovementType.PrepMade && m.Type != MovementType.Surplus
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

    /// <summary>Потеря по журналу: списание или недостача, найденная при инвентаризации.</summary>
    private sealed record Loss(DateTimeOffset At, MovementType Type, decimal Quantity, string? Note, Guid ProductId, string Name, Unit Unit, decimal Cost)
    {
        public decimal Value => Quantity * Cost;
        public string Reason => Type == MovementType.Shortage ? ShortageReason : Note ?? "Без причины";
    }

    /// <summary>
    /// Потери за период по закупочным ценам: списания и недостача по инвентаризации.
    /// Излишек возвращается отдельной суммой: найденное лишнее не отменяет того, что пропало.
    /// </summary>
    private static async Task<(List<Loss> Rows, decimal Surplus)> LossesAsync(AppDbContext db, DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
    {
        // Потеря оценивается по цене партии, из которой списано, а не по сегодняшней цене продукта.
        var moves = await (
                from m in db.Movements
                where (m.Type == MovementType.WriteOff || m.Type == MovementType.Shortage || m.Type == MovementType.Surplus)
                    && m.At >= since && (until == null || m.At < until)
                join b in db.Batches on m.BatchId equals b.Id
                select new Loss(m.At, m.Type, m.Quantity, m.Note, m.ProductId, m.Product!.Name, m.Product.Unit, b.UnitCost))
            .ToListAsync(ct);
        return (moves.Where(m => m.Type != MovementType.Surplus).ToList(), moves.Where(m => m.Type == MovementType.Surplus).Sum(m => m.Value));
    }

    private static IEnumerable<WriteOffReason> ByReason(IEnumerable<Loss> rows) => rows
        .GroupBy(r => r.Reason)
        .Select(g => new WriteOffReason(g.Key, g.Sum(r => r.Value)))
        .OrderByDescending(r => r.Value);

    /// <summary>
    /// Цена стопа по блюдам за период [since, now]. Если задан from, скорость продаж считается за весь период,
    /// а простой и потери — только начиная с from: так отчёт за смену оценивает один день по спросу за две недели.
    /// </summary>
    private static async Task<List<StopCostRow>> StopCostAsync(
        AppDbContext db, StockOptions options, DateTimeOffset since, DateTimeOffset now, CancellationToken ct, DateTimeOffset? from = null)
    {
        var stops = (await db.StopLogs.AsNoTracking().Where(l => l.StartedAt <= now && (l.EndedAt == null || l.EndedAt >= since)).ToListAsync(ct)).ToLookup(l => l.DishId);
        var sold = await db.Orders
            .Where(o => o.Status == OrderStatus.Accepted && o.CreatedAt >= since && o.CreatedAt <= now)
            .SelectMany(o => o.Items)
            .GroupBy(i => i.DishId)
            .Select(g => new { DishId = g.Key, Portions = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(g => g.DishId, g => g.Portions, ct);
        var restaurant = await db.CurrentRestaurantAsync(ct);
        var (start, end) = StopCost.Shift(restaurant.ShiftStart, restaurant.ShiftEnd);
        var zone = TimeSpan.FromHours(options.UtcOffsetHours);

        return (await db.Dishes.AsNoTracking().ToListAsync(ct))
            .Where(d => stops.Contains(d.Id))
            .Select(d => StopCost.Of(d, stops[d.Id].Select(l => (l.StartedAt, l.EndedAt is { } ended && ended < now ? ended : now)), sold.GetValueOrDefault(d.Id), since, now, start, end, zone, from))
            .Where(r => r.StopHours > 0)
            .OrderByDescending(r => r.LostRevenue).ThenByDescending(r => r.StopHours)
            .ToList();
    }

    private static async Task<IEnumerable<PurchaseRow>> PurchasesAsync(AppDbContext db, DateTimeOffset now, StockOptions options, CancellationToken ct)
    {
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
    }
}
