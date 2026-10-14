using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public enum BatchStatus { Fresh, ExpiringSoon, Expired }

/// <param name="FreshValue">Стоимость годного остатка по закупочной цене; пусто для ролей, которым она не видна.</param>
public sealed record StockRow(
    Guid ProductId, string ProductName, Unit Unit,
    decimal Fresh, decimal Expired, decimal? FreshValue,
    int Batches, DateTimeOffset? NearestExpiry);

public sealed record BatchRow(
    Guid Id, Guid ProductId, string ProductName, Unit Unit, string SupplyNumber,
    decimal Quantity, decimal Remaining, decimal? Value, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt, double HoursLeft, BatchStatus Status,
    DateTimeOffset? OpenedAt, int? OpenedShelfLifeHours);

public sealed record StopListRow(
    Guid DishId, string DishName, string? PhotoUrl, decimal Price, DishStatus Status, int Portions,
    Guid LimitingProductId, string LimitingProductName, StopReason? Reason,
    DateTimeOffset? StopForecastAt, decimal? Cost, string? Category);

/// <param name="Reason">Нужна только для годной партии; просроченная списывается без причины.</param>
public sealed record WriteOffRequest(WriteOffCause? Reason);

public sealed record MovementRow(
    long Id, DateTimeOffset At, MovementType Type, Guid ProductId, string ProductName, Unit Unit,
    Guid BatchId, decimal Quantity, Guid? OrderId, string? Note, string Actor, int? OrderNumber);

public sealed class StockOptions
{
    /// <summary>За сколько часов до конца срока партия считается истекающей.</summary>
    public int ExpiringSoonHours { get; set; } = 24;
    /// <summary>Горизонт горячих предложений: учитываются партии, истекающие в эти часы.</summary>
    public double HotOfferHours { get; set; } = 48;
    public bool AutoWriteOff { get; set; } = true;
    /// <summary>Записывать историю стоп-листа для аналитики и поднимать сигналы о сроках и стопах.</summary>
    public bool TrackStops { get; set; } = true;
    /// <summary>Сколько часов просрочка ждёт ручного списания, прежде чем спишется сама.</summary>
    public double AutoWriteOffAfterHours { get; set; } = 12;
    /// <summary>За сколько дней считается средний расход, пока нет полных дней истории.</summary>
    public int ConsumptionDays { get; set; } = 7;
    /// <summary>Сколько дней истории берётся для прогноза расхода по дням недели.</summary>
    public int DemandHistoryDays { get; set; } = 28;
    /// <summary>На сколько часов вперёд должно хватать запаса; на этот же срок строится прогноз стопа.</summary>
    public double PurchaseCoverHours { get; set; } = 72;
    /// <summary>Часовой пояс ресторана для отчётов по дням.</summary>
    public double UtcOffsetHours { get; set; } = 3;
}

public static class StockEndpoints
{
    public static void MapStock(this RouteGroupBuilder api)
    {
        var stock = api.MapGroup("/stock").WithTags("Склад");

        // Остатки — в кэше чтений (см. ReadCache): их перечитывает каждый открытый экран склада.
        stock.MapGet("", (HttpContext http, ReadCache cache, Tenant tenant, AppDbContext db, TimeProvider time, ClaimsPrincipal user) =>
            cache.GetAsync(http, tenant.Id, $"stock|{user.SeesCosts()}", async () =>
        {
            var ct = CancellationToken.None;
            var now = time.GetUtcNow();
            var costs = user.SeesCosts();
            var totals = await db.Batches
                .Where(b => b.Remaining > 0)
                .GroupBy(b => b.ProductId)
                .Select(g => new
                {
                    ProductId = g.Key,
                    Fresh = g.Sum(b => b.ExpiresAt > now ? b.Remaining : 0m),
                    Expired = g.Sum(b => b.ExpiresAt <= now ? b.Remaining : 0m),
                    FreshValue = g.Sum(b => b.ExpiresAt > now ? b.Remaining * b.UnitCost : 0m),
                    Batches = g.Count(),
                    NearestExpiry = g.Min(b => b.ExpiresAt),
                    // Ближайший срок годной партии: в этот миг годный остаток станет просроченным без записи в базу.
                    NextExpiry = g.Min(b => b.ExpiresAt > now ? (DateTimeOffset?)b.ExpiresAt : null),
                })
                .ToDictionaryAsync(r => r.ProductId, ct);

            // Продукты без остатка тоже в списке: по ним видно, чего на складе нет совсем.
            var rows = (await db.Products.OrderBy(p => p.Name).ToListAsync(ct)).Select(p =>
            {
                var t = totals.GetValueOrDefault(p.Id);
                return new StockRow(p.Id, p.Name, p.Unit, t?.Fresh ?? 0, t?.Expired ?? 0, costs ? t?.FreshValue ?? 0 : null, t?.Batches ?? 0, t?.NearestExpiry);
            }).ToList();
            return (rows, totals.Values.Min(t => t.NextExpiry));
        }))
        .Produces<List<StockRow>>();

        stock.MapGet("/batches", async (AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct, Guid? productId = null) =>
        {
            var now = time.GetUtcNow();
            var costs = user.SeesCosts();
            var soon = now.AddHours(options.ExpiringSoonHours);
            var batches = await db.Batches
                .Where(b => b.Remaining > 0 && (productId == null || b.ProductId == productId))
                .OrderBy(b => b.ExpiresAt)
                .Select(b => new { b.Id, b.ProductId, ProductName = b.Product!.Name, b.Product.Unit, SupplyNumber = b.Supply!.Number, ReceivedAt = b.Supply.CreatedAt, b.Quantity, b.Remaining, Cost = b.UnitCost, b.ExpiresAt, b.OpenedAt, b.Product.OpenedShelfLifeHours })
                .ToListAsync(ct);

            return batches.Select(b => new BatchRow(
                b.Id, b.ProductId, b.ProductName, b.Unit, b.SupplyNumber, b.Quantity, b.Remaining, costs ? b.Remaining * b.Cost : null, b.ReceivedAt, b.ExpiresAt,
                Math.Round((b.ExpiresAt - now).TotalHours, 1),
                b.ExpiresAt <= now ? BatchStatus.Expired : b.ExpiresAt <= soon ? BatchStatus.ExpiringSoon : BatchStatus.Fresh,
                b.OpenedAt, b.OpenedShelfLifeHours));
        });

        stock.MapPost("/batches/{id:guid}/write-off", async (Guid id, WriteOffRequest? req, WriteOffService service, ClaimsPrincipal user, CancellationToken ct) =>
            await service.WriteOffAsync(id, user.Identity?.Name ?? "", req?.Reason, ct) switch
            {
                WriteOffResult.NotFound => Results.NotFound(),
                WriteOffResult.ReasonRequired => new Errors().Add("reason", "Партия ещё годна. Укажите причину списания: Spoiled (порча при хранении) или Defective (брак поставки).").ToResult(),
                WriteOffResult.Empty => Results.Problem("В партии ничего не осталось.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.NoContent(),
            }).RequireRoles(Access.Stock);

        // Стоп-лист запрашивает каждый терминал официанта: ответ берётся из кэша чтений и считается заново
        // только после изменения данных ресторана или когда истекает ближайшая партия (см. ReadCache).
        // Решение о продаже по нему не принимается: заказ сам читает партии под FOR UPDATE (OrderService).
        api.MapGet("/stop-list", (HttpContext http, ReadCache cache, Tenant tenant, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user) =>
            cache.GetAsync(http, tenant.Id, $"stop-list|{user.SeesCosts()}", async () =>
        {
            var ct = CancellationToken.None;
            var now = time.GetUtcNow();
            var costs = user.SeesCosts();
            var totals = await db.LoadTotalsAsync(now, ct);
            var fresh = await db.LoadFreshBatchesAsync(now, ct);
            var batches = fresh.ToLookup(b => b.ProductId);
            var perHour = await db.LoadConsumptionPerHourAsync(now, options, ct);
            var rows = (await db.LoadDishesAsync(ct))
                .Select(d =>
                {
                    var a = Availability.Of(d, totals);
                    var limiting = d.Recipe.First(r => r.ProductId == a.LimitingProductId).Product!;
                    var forecast = a.Status == DishStatus.Stop ? null : Forecast.DishStopAt(d, batches, perHour, now, options.PurchaseCoverHours);
                    return new StopListRow(d.Id, d.Name, d.PhotoUrl, d.Price, a.Status, a.Portions, limiting.Id, limiting.Name, a.Reason, forecast,
                        costs ? d.Recipe.Sum(r => r.Quantity * r.Product!.Cost) : null, d.Category);
                })
                .OrderByDescending(r => r.Status).ThenBy(r => r.Portions).ThenBy(r => r.DishName)
                .ToList();
            return (rows, fresh.FirstOrDefault()?.ExpiresAt);
        }))
        .Produces<List<StopListRow>>()
        .WithTags("Стоп-лист");

        // batchId — история одной партии: приёмка, вскрытие, заказы и списания, которые её касались.
        api.MapGet("/movements", async (AppDbContext db, CancellationToken ct, int take = 100, Guid? batchId = null) =>
            await db.Movements
                .Where(m => batchId == null || m.BatchId == batchId)
                .OrderByDescending(m => m.Id)
                .Take(Math.Clamp(take, 1, 500))
                .Select(m => new MovementRow(m.Id, m.At, m.Type, m.ProductId, m.Product!.Name, m.Product.Unit, m.BatchId, m.Quantity, m.OrderId, m.Note, m.Actor,
                    db.Orders.Where(o => o.Id == m.OrderId).Select(o => (int?)o.Number).FirstOrDefault()))
                .ToListAsync(ct))
        .WithTags("Склад");
    }
}
