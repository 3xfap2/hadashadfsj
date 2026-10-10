using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>
/// Ведёт историю стоп-листа: когда блюдо ушло в стоп, из-за какого продукта и когда вернулось.
/// По ней аналитика считает часы простоя. Вызывается раз в минуту, поэтому точность — до минуты.
/// </summary>
public sealed class StopTracker(AppDbContext db, TimeProvider time)
{
    public async Task TrackAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var totals = await db.LoadTotalsAsync(now, ct);
        var stopped = (await db.LoadDishesAsync(ct))
            .Select(d => (Dish: d, State: Availability.Of(d, totals)))
            .Where(x => x.State.Status == DishStatus.Stop)
            .ToDictionary(x => x.Dish.Id, x => x.State);

        var open = await db.StopLogs.Where(l => l.EndedAt == null).ToListAsync(ct);
        foreach (var log in open)
        {
            // Блюдо вернулось в продажу либо стоит уже по другой причине — прежний интервал закрывается.
            var same = stopped.TryGetValue(log.DishId, out var state) && state.LimitingProductId == log.ProductId && state.Reason == log.Reason;
            if (!same) log.EndedAt = now;
        }

        var continuing = open.Where(l => l.EndedAt == null).Select(l => l.DishId).ToHashSet();
        foreach (var (dishId, state) in stopped.Where(s => !continuing.Contains(s.Key)))
            db.StopLogs.Add(new StopLog { DishId = dishId, ProductId = state.LimitingProductId, Reason = state.Reason!.Value, StartedAt = now });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Сколько часов интервал стопа пересекается с периодом [from, now].</summary>
    public static double Hours(StopLog log, DateTimeOffset from, DateTimeOffset now)
    {
        var start = log.StartedAt > from ? log.StartedAt : from;
        var end = log.EndedAt ?? now;
        return end > start ? (end - start).TotalHours : 0;
    }
}
