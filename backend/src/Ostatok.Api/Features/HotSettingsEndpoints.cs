using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record HotDishRequest(bool Pinned, bool Hidden);

/// <summary>
/// Настройка горячих предложений: менеджер меняет правила расчёта и правит сам список —
/// закрепляет блюдо наверху или убирает его из предложений.
/// </summary>
public static class HotSettingsEndpoints
{
    public static void MapHotSettings(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/hot-offers").WithTags("Горячие предложения").RequireRoles(Access.Catalog);

        settings.MapGet("/settings", async (AppDbContext db, StockOptions options, CancellationToken ct) =>
            await db.LoadHotRulesAsync(options, ct));

        settings.MapPut("/settings", async (HotRules req, AppDbContext db, CancellationToken ct) =>
        {
            var errors = new Errors()
                .Check(req.HorizonHours is >= 6 and <= 168, "horizonHours", "Горизонт — от 6 до 168 часов.")
                .Check(req.UrgencyWeight is >= 0 and <= 100, "urgencyWeight", "Вес срока — от 0 до 100.")
                .Check(req.MaxDiscountPercent is >= 0 and <= 70, "maxDiscountPercent", "Наибольшая скидка — от 0 до 70%.");
            if (errors.Any) return errors.ToResult();

            var row = await db.HotSettings.FirstOrDefaultAsync(ct);
            if (row is null) db.HotSettings.Add(row = new HotSettings { RestaurantId = db.TenantId });
            (row.HorizonHours, row.UrgencyWeight, row.ShareAcrossDishes, row.MaxDiscountPercent) =
                (req.HorizonHours, req.UrgencyWeight, req.ShareAcrossDishes, req.MaxDiscountPercent);
            await db.SaveChangesAsync(ct);
            return Results.Ok(req);
        });

        // Правка самого списка: закрепить блюдо первым или убрать из предложений.
        settings.MapPut("/dishes/{id:guid}", async (Guid id, HotDishRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var dish = await db.Dishes.FindAsync([id], ct);
            if (dish is null) return Results.NotFound();
            // Скрытое блюдо закрепить нельзя: скрытие важнее.
            (dish.HotPinned, dish.HotHidden) = (req.Pinned && !req.Hidden, req.Hidden);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
