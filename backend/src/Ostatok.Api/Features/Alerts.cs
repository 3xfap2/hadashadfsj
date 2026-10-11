using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record AlertResponse(long Id, DateTimeOffset At, AlertKind Kind, string Title, string Detail, DateTimeOffset? ExpiresAt, bool Read);
public sealed record AlertsResponse(int Unread, IEnumerable<AlertResponse> Items);

/// <summary>
/// Сигналы: партия скоро истекает, партия просрочена, блюдо ушло в стоп или вернулось.
/// Каждый сигнал создаётся один раз и хранится, пока его не прочитают, — он не пропадёт, если экран был закрыт.
/// Прочитанность у каждого сотрудника своя: запоминается номер последнего сигнала, который он видел.
/// </summary>
public sealed class AlertService(AppDbContext db, TimeProvider time, StockOptions options)
{
    /// <summary>Создаёт сигналы по партиям, у которых срок подошёл к порогу или истёк. Возвращает число новых.</summary>
    public async Task<int> RaiseBatchAlertsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var soon = now.AddHours(options.ExpiringSoonHours);
        var batches = await db.Batches
            .Where(b => b.Remaining > 0 && b.ExpiresAt <= soon)
            .Select(b => new { b.Id, b.Remaining, b.ExpiresAt, Product = b.Product!.Name, b.Product.Unit, Supply = b.Supply!.Number })
            .ToListAsync(ct);
        if (batches.Count == 0) return 0;

        var ids = batches.Select(b => b.Id).ToList();
        var raised = (await db.Alerts.Where(a => a.BatchId != null && ids.Contains(a.BatchId.Value)).Select(a => new { a.BatchId, a.Kind }).ToListAsync(ct))
            .Select(a => (a.BatchId!.Value, a.Kind))
            .ToHashSet();

        var created = 0;
        foreach (var batch in batches)
        {
            var kind = batch.ExpiresAt <= now ? AlertKind.BatchExpired : AlertKind.BatchExpiring;
            if (!raised.Add((batch.Id, kind))) continue;
            db.Alerts.Add(new Alert
            {
                At = now, Kind = kind, BatchId = batch.Id, ExpiresAt = batch.ExpiresAt,
                Title = kind == AlertKind.BatchExpired ? $"{batch.Product}: срок годности истёк" : $"{batch.Product}: срок годности истекает",
                Detail = $"{Ru.Qty(batch.Remaining, batch.Unit)} из поставки {batch.Supply}",
            });
            created++;
        }
        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }
}

public static class AlertEndpoints
{
    public static void MapAlerts(this RouteGroupBuilder api)
    {
        var alerts = api.MapGroup("/alerts").WithTags("Сигналы");

        alerts.MapGet("", async (AppDbContext db, ClaimsPrincipal user, CancellationToken ct, int take = 30) =>
        {
            var login = user.Identity?.Name;
            var read = await db.Users.Where(u => u.Login == login).Select(u => u.LastReadAlertId).FirstOrDefaultAsync(ct);
            var items = await db.Alerts.OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
            return new AlertsResponse(
                await db.Alerts.CountAsync(a => a.Id > read, ct),
                items.Select(a => new AlertResponse(a.Id, a.At, a.Kind, a.Title, a.Detail, a.ExpiresAt, a.Id <= read)));
        });

        // upTo — последний сигнал, который сотрудник увидел в списке: пришедшие позже остаются непрочитанными.
        alerts.MapPost("/read", async (AppDbContext db, ClaimsPrincipal user, EventBus bus, CancellationToken ct, long? upTo = null) =>
        {
            var login = user.Identity?.Name;
            var last = await db.Alerts.Where(a => upTo == null || a.Id <= upTo).MaxAsync(a => (long?)a.Id, ct) ?? 0;
            await db.Users.Where(u => u.Login == login && u.LastReadAlertId < last).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastReadAlertId, last), ct);
            bus.Publish(db.TenantId);
            return Results.NoContent();
        });
    }
}
