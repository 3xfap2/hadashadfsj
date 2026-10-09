using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

public static class StockQueries
{
    public static async Task<StockTotals> LoadTotalsAsync(this AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Batches
            .Where(b => b.Remaining > 0)
            .GroupBy(b => b.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Fresh = g.Sum(b => b.ExpiresAt > now ? b.Remaining : 0m),
                Expired = g.Sum(b => b.ExpiresAt <= now ? b.Remaining : 0m),
            })
            .ToListAsync(ct);

        return new StockTotals(
            rows.ToDictionary(r => r.ProductId, r => r.Fresh),
            rows.Where(r => r.Expired > 0).Select(r => r.ProductId).ToHashSet());
    }

    public static Task<List<Batch>> LoadFreshBatchesAsync(this AppDbContext db, DateTimeOffset now, CancellationToken ct) =>
        db.Batches
            .AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.Remaining > 0 && b.ExpiresAt > now)
            .OrderBy(b => b.ExpiresAt).ThenBy(b => b.Id)
            .ToListAsync(ct);

    public static Task<List<Dish>> LoadDishesAsync(this AppDbContext db, CancellationToken ct) =>
        db.Dishes.AsNoTracking().Include(d => d.Recipe).ThenInclude(r => r.Product).ToListAsync(ct);

    /// <summary>
    /// Средний расход продуктов в час по заказам за последние дни.
    /// Делится на фактическую длину истории, но не меньше суток: по паре заказов за час
    /// нельзя судить о дневном расходе.
    /// </summary>
    public static async Task<Dictionary<Guid, decimal>> LoadConsumptionPerHourAsync(
        this AppDbContext db, DateTimeOffset now, int days, CancellationToken ct)
    {
        var since = now.AddDays(-days);
        var orders = db.Movements.Where(m => m.Type == MovementType.Order && m.At >= since);
        var totals = await orders
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(m => m.Quantity), First = g.Min(m => m.At) })
            .ToListAsync(ct);
        if (totals.Count == 0) return [];

        var hours = (decimal)Math.Max(24, (now - totals.Min(t => t.First)).TotalHours);
        return totals.ToDictionary(t => t.ProductId, t => t.Quantity / hours);
    }
}
