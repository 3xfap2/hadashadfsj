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

    /// <summary>Правила горячих предложений: настроенные менеджером или исходные.</summary>
    public static async Task<HotRules> LoadHotRulesAsync(this AppDbContext db, Features.StockOptions options, CancellationToken ct) =>
        await db.HotSettings.AsNoTracking().FirstOrDefaultAsync(ct) is { } row
            ? new HotRules(row.HorizonHours, row.UrgencyWeight, row.ShareAcrossDishes, row.MaxDiscountPercent)
            : new HotRules(options.HotOfferHours);

    /// <summary>Ресторан, от имени которого идёт запрос.</summary>
    public static Task<Restaurant> CurrentRestaurantAsync(this AppDbContext db, CancellationToken ct) =>
        db.Restaurants.AsNoTracking().FirstAsync(r => r.Id == db.TenantId, ct);

    /// <summary>
    /// Следующий номер заготовки ресторана. Счётчик увеличивается одной командой, поэтому два одновременных
    /// запроса получают разные номера.
    /// </summary>
    public static async Task<int> NextPrepNumberAsync(this AppDbContext db, CancellationToken ct) =>
        (await db.Database
            .SqlQuery<int>($"""UPDATE "Restaurants" SET "LastPrepNumber" = "LastPrepNumber" + 1 WHERE "Id" = {db.TenantId} RETURNING "LastPrepNumber" """)
            .ToListAsync(ct)).Single();

    public static async Task<int> NextCheckNumberAsync(this AppDbContext db, CancellationToken ct) =>
        (await db.Database
            .SqlQuery<int>($"""UPDATE "Restaurants" SET "LastCheckNumber" = "LastCheckNumber" + 1 WHERE "Id" = {db.TenantId} RETURNING "LastCheckNumber" """)
            .ToListAsync(ct)).Single();

    public static async Task<int> NextInventoryNumberAsync(this AppDbContext db, CancellationToken ct) =>
        (await db.Database
            .SqlQuery<int>($"""UPDATE "Restaurants" SET "LastInventoryNumber" = "LastInventoryNumber" + 1 WHERE "Id" = {db.TenantId} RETURNING "LastInventoryNumber" """)
            .ToListAsync(ct)).Single();

    /// <summary>
    /// Пересчитывает цену заготовок, в состав которых входят продукты с изменившейся ценой.
    /// Вызывается до сохранения: новые цены берутся из уже изменённых записей.
    /// </summary>
    public static async Task RecalcPrepCostsAsync(this AppDbContext db, IReadOnlyCollection<Guid> changed, CancellationToken ct)
    {
        var preps = await db.Products.Include(p => p.PrepRecipe).Where(p => p.PrepRecipe.Any(i => changed.Contains(i.ProductId))).ToListAsync(ct);
        if (preps.Count == 0) return;

        var ids = preps.SelectMany(p => p.PrepRecipe).Select(i => i.ProductId).Distinct().ToArray();
        var costs = (await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id, p => p.Cost);
        foreach (var prep in preps)
            prep.Cost = Math.Round(prep.PrepRecipe.Sum(i => i.Quantity * costs[i.ProductId]), 2);
    }

    public static Task<List<Dish>> LoadDishesAsync(this AppDbContext db, CancellationToken ct) =>
        db.Dishes.AsNoTracking().Include(d => d.Recipe).ThenInclude(r => r.Product).ToListAsync(ct);

    /// <summary>
    /// Ожидаемый расход продуктов в час на ближайшие дни. Если в истории есть хотя бы один полный день,
    /// прогноз строится по дням недели (см. <see cref="Demand"/>); иначе берётся средний расход за то, что есть.
    /// </summary>
    public static async Task<Dictionary<Guid, decimal>> LoadConsumptionPerHourAsync(
        this AppDbContext db, DateTimeOffset now, Features.StockOptions options, CancellationToken ct)
    {
        var zone = TimeSpan.FromHours(options.UtcOffsetHours);
        var today = DateOnly.FromDateTime(now.ToOffset(zone).Date);
        var from = new DateTimeOffset(today.AddDays(-options.DemandHistoryDays).ToDateTime(TimeOnly.MinValue), zone).ToUniversalTime();
        // Сутки ресторана начинаются в его полночь, поэтому перед группировкой время сдвигается на часовой пояс.
        var daily = await db.Database
            .SqlQuery<DailyUse>($"""
                SELECT "ProductId", (("At" AT TIME ZONE 'UTC') + {zone})::date AS "Day", SUM("Quantity") AS "Quantity"
                FROM "Movements"
                WHERE "Type" IN ({nameof(MovementType.Order)}, {nameof(MovementType.PrepUse)}) AND "At" >= {from} AND "RestaurantId" = {db.TenantId}
                GROUP BY 1, 2
                """)
            .ToListAsync(ct);
        var byWeekday = Demand.PerHour(daily, today, (int)Math.Ceiling(options.PurchaseCoverHours / 24));
        if (byWeekday.Count > 0) return byWeekday;

        return await LoadAverageConsumptionAsync(db, now, options.ConsumptionDays, ct);
    }

    /// <summary>
    /// Средний расход в час, пока полных дней истории нет. Делится на фактическую длину истории,
    /// но не меньше суток: по паре заказов за час нельзя судить о дневном расходе.
    /// </summary>
    private static async Task<Dictionary<Guid, decimal>> LoadAverageConsumptionAsync(
        AppDbContext db, DateTimeOffset now, int days, CancellationToken ct)
    {
        var since = now.AddDays(-days);
        // Сырьё, ушедшее в заготовки, — такой же расход, как продажа блюда.
        var orders = db.Movements.Where(m => (m.Type == MovementType.Order || m.Type == MovementType.PrepUse) && m.At >= since);
        var totals = await orders
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(m => m.Quantity), First = g.Min(m => m.At) })
            .ToListAsync(ct);
        if (totals.Count == 0) return [];

        var hours = (decimal)Math.Max(24, (now - totals.Min(t => t.First)).TotalHours);
        return totals.ToDictionary(t => t.ProductId, t => t.Quantity / hours);
    }
}
