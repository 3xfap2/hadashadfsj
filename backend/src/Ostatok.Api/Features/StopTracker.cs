using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>
/// Ведёт историю стоп-листа: когда блюдо ушло в стоп, из-за какого продукта и когда вернулось.
/// По ней аналитика считает часы простоя. Вызывается раз в 20 секунд, поэтому точность — до минуты.
/// </summary>
public sealed class StopTracker(AppDbContext db, TimeProvider time)
{
    public async Task TrackAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var totals = await db.LoadTotalsAsync(now, ct);
        var dishes = await db.LoadDishesAsync(ct);
        var names = dishes.ToDictionary(d => d.Id, d => d.Name);
        var products = dishes.SelectMany(d => d.Recipe).Select(r => r.Product!).DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => p.Name);
        var stopped = dishes
            .Select(d => (Dish: d, State: Availability.Of(d, totals)))
            .Where(x => x.State.Status == DishStatus.Stop)
            .ToDictionary(x => x.Dish.Id, x => x.State);

        var open = await db.StopLogs.Where(l => l.EndedAt == null).ToListAsync(ct);
        foreach (var log in open)
        {
            // Блюдо вернулось в продажу либо стоит уже по другой причине — прежний интервал закрывается.
            // У ручного стопа продукт ни при чём: смена самого дефицитного продукта его не прерывает.
            var same = stopped.TryGetValue(log.DishId, out var state) && state.Reason == log.Reason
                && (state.Reason == StopReason.Manual || state.LimitingProductId == log.ProductId);
            if (same) continue;
            log.EndedAt = now;
            // Вернулось в продажу — отдельный сигнал; смена причины стопа сигналом о возврате не считается.
            if (!stopped.ContainsKey(log.DishId) && names.TryGetValue(log.DishId, out var name))
                db.Alerts.Add(new Alert { At = now, Kind = AlertKind.DishBack, DishId = log.DishId, Title = $"{name}: снова в продаже", Detail = "Блюдо вышло из стоп-листа" });
        }

        var continuing = open.Where(l => l.EndedAt == null).Select(l => l.DishId).ToHashSet();
        // Блюдо не выходило из стопа, сменилась только причина: история пишется новым интервалом, а сигнал повторно не нужен.
        var wasStopped = open.Where(l => l.EndedAt == now).Select(l => l.DishId).ToHashSet();
        foreach (var (dishId, state) in stopped.Where(s => !continuing.Contains(s.Key)))
        {
            db.StopLogs.Add(new StopLog { DishId = dishId, ProductId = state.LimitingProductId, Reason = state.Reason!.Value, StartedAt = now });
            if (wasStopped.Contains(dishId)) continue;
            db.Alerts.Add(new Alert
            {
                At = now, Kind = AlertKind.DishStopped, DishId = dishId, Title = $"{names[dishId]}: в стоп-листе",
                Detail = state.Reason == StopReason.Manual
                    ? "Снято с продажи вручную"
                    : $"{products.GetValueOrDefault(state.LimitingProductId, "Продукт")}: {Reasons[state.Reason!.Value]}",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static readonly Dictionary<StopReason, string> Reasons = new()
    {
        [StopReason.Expired] = "просрочено",
        [StopReason.OutOfStock] = "нет на складе",
        [StopReason.NotEnough] = "не хватает на порцию",
        [StopReason.Manual] = "снято вручную",
    };

    /// <summary>Сколько часов интервал стопа пересекается с периодом [from, now].</summary>
    public static double Hours(StopLog log, DateTimeOffset from, DateTimeOffset now)
    {
        var start = log.StartedAt > from ? log.StartedAt : from;
        var end = log.EndedAt ?? now;
        return end > start ? (end - start).TotalHours : 0;
    }
}
