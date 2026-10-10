using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public enum BatchStatus { Fresh, ExpiringSoon, Expired }

public sealed record StockRow(
    Guid ProductId, string ProductName, Unit Unit,
    decimal Fresh, decimal Expired, decimal FreshValue,
    int Batches, DateTimeOffset? NearestExpiry);

public sealed record BatchRow(
    Guid Id, Guid ProductId, string ProductName, Unit Unit, string SupplyNumber,
    decimal Quantity, decimal Remaining, decimal Value, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt, double HoursLeft, BatchStatus Status);

public sealed record StopListRow(
    Guid DishId, string DishName, string? PhotoUrl, decimal Price, DishStatus Status, int Portions,
    Guid LimitingProductId, string LimitingProductName, StopReason? Reason,
    DateTimeOffset? StopForecastAt);

public sealed record MovementRow(
    long Id, DateTimeOffset At, MovementType Type, Guid ProductId, string ProductName, Unit Unit,
    Guid BatchId, decimal Quantity, Guid? OrderId, string? Note, string Actor);

public sealed class StockOptions
{
    /// <summary>За сколько часов до конца срока партия считается истекающей.</summary>
    public int ExpiringSoonHours { get; set; } = 24;
    /// <summary>Горизонт горячих предложений: учитываются партии, истекающие в эти часы.</summary>
    public double HotOfferHours { get; set; } = 48;
    public bool AutoWriteOff { get; set; } = true;
    /// <summary>Сколько часов просрочка ждёт ручного списания, прежде чем спишется сама.</summary>
    public double AutoWriteOffAfterHours { get; set; } = 12;
    /// <summary>За сколько дней считается средний расход продуктов.</summary>
    public int ConsumptionDays { get; set; } = 7;
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

        stock.MapGet("", async (AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var totals = await db.Batches
                .Where(b => b.Remaining > 0)
                .GroupBy(b => b.ProductId)
                .Select(g => new
                {
                    ProductId = g.Key,
                    Fresh = g.Sum(b => b.ExpiresAt > now ? b.Remaining : 0m),
                    Expired = g.Sum(b => b.ExpiresAt <= now ? b.Remaining : 0m),
                    Batches = g.Count(),
                    NearestExpiry = g.Min(b => b.ExpiresAt),
                })
                .ToDictionaryAsync(r => r.ProductId, ct);

            // Продукты без остатка тоже в списке: по ним видно, чего на складе нет совсем.
            return (await db.Products.OrderBy(p => p.Name).ToListAsync(ct)).Select(p =>
            {
                var t = totals.GetValueOrDefault(p.Id);
                return new StockRow(p.Id, p.Name, p.Unit, t?.Fresh ?? 0, t?.Expired ?? 0, (t?.Fresh ?? 0) * p.Cost, t?.Batches ?? 0, t?.NearestExpiry);
            });
        });

        stock.MapGet("/batches", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct, Guid? productId = null) =>
        {
            var now = time.GetUtcNow();
            var soon = now.AddHours(options.ExpiringSoonHours);
            var batches = await db.Batches
                .Where(b => b.Remaining > 0 && (productId == null || b.ProductId == productId))
                .OrderBy(b => b.ExpiresAt)
                .Select(b => new { b.Id, b.ProductId, ProductName = b.Product!.Name, b.Product.Unit, SupplyNumber = b.Supply!.Number, ReceivedAt = b.Supply.CreatedAt, b.Quantity, b.Remaining, b.Product.Cost, b.ExpiresAt })
                .ToListAsync(ct);

            return batches.Select(b => new BatchRow(
                b.Id, b.ProductId, b.ProductName, b.Unit, b.SupplyNumber, b.Quantity, b.Remaining, b.Remaining * b.Cost, b.ReceivedAt, b.ExpiresAt,
                Math.Round((b.ExpiresAt - now).TotalHours, 1),
                b.ExpiresAt <= now ? BatchStatus.Expired : b.ExpiresAt <= soon ? BatchStatus.ExpiringSoon : BatchStatus.Fresh));
        });

        stock.MapPost("/batches/{id:guid}/write-off", async (Guid id, WriteOffService service, ClaimsPrincipal user, CancellationToken ct) =>
            await service.WriteOffAsync(id, user.Identity?.Name ?? "", ct) switch
            {
                WriteOffResult.NotFound => Results.NotFound(),
                WriteOffResult.Empty => Results.Problem("В партии ничего не осталось.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.NoContent(),
            }).RequireRoles(Access.Stock);

        api.MapGet("/stop-list", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var totals = await db.LoadTotalsAsync(now, ct);
            var batches = (await db.LoadFreshBatchesAsync(now, ct)).ToLookup(b => b.ProductId);
            var perHour = await db.LoadConsumptionPerHourAsync(now, options.ConsumptionDays, ct);
            return (await db.LoadDishesAsync(ct))
                .Select(d =>
                {
                    var a = Availability.Of(d, totals);
                    var limiting = d.Recipe.First(r => r.ProductId == a.LimitingProductId).Product!;
                    var forecast = a.Status == DishStatus.Stop ? null : Forecast.DishStopAt(d, batches, perHour, now, options.PurchaseCoverHours);
                    return new StopListRow(d.Id, d.Name, d.PhotoUrl, d.Price, a.Status, a.Portions, limiting.Id, limiting.Name, a.Reason, forecast);
                })
                .OrderByDescending(r => r.Status).ThenBy(r => r.Portions).ThenBy(r => r.DishName);
        })
        .WithTags("Стоп-лист");

        api.MapGet("/movements", async (AppDbContext db, CancellationToken ct, int take = 100) =>
            await db.Movements
                .OrderByDescending(m => m.Id)
                .Take(Math.Clamp(take, 1, 500))
                .Select(m => new MovementRow(m.Id, m.At, m.Type, m.ProductId, m.Product!.Name, m.Product.Unit, m.BatchId, m.Quantity, m.OrderId, m.Note, m.Actor))
                .ToListAsync(ct))
        .WithTags("Склад");
    }
}
