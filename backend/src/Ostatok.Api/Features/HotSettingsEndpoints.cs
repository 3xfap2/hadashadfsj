using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record HotDishRequest(bool Pinned, bool Hidden);
/// <param name="Note">Причина для официанта, необязательно: «повар Иван сегодня готовит фирменное».</param>
public sealed record HotAddRequest(string? Note);

/// <summary>
/// Настройка горячих предложений: менеджер меняет правила расчёта и правит сам список —
/// закрепляет блюдо наверху, убирает его из предложений или добавляет своё.
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
            // Скрытое блюдо закрепить нельзя: скрытие важнее. Оно же снимает ручное добавление.
            (dish.HotPinned, dish.HotHidden) = (req.Pinned && !req.Hidden, req.Hidden);
            if (req.Hidden) (dish.HotAdded, dish.HotNote) = (false, null);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Добавить блюдо в предложения вручную — например, повар сегодня готовит его особенно. Официант видит его
        // с пометкой «добавлено управляющим» и причиной; в план спасения и суммы оно не входит.
        settings.MapPut("/dishes/{id:guid}/added", async (Guid id, HotAddRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            var errors = new Errors().Check(note is not { Length: > 200 }, "note", "Причина — до 200 символов.");
            if (errors.Any) return errors.ToResult();
            var dish = await db.Dishes.FindAsync([id], ct);
            if (dish is null) return Results.NotFound();
            (dish.HotAdded, dish.HotNote, dish.HotHidden) = (true, note, false);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        settings.MapDelete("/dishes/{id:guid}/added", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var dish = await db.Dishes.FindAsync([id], ct);
            if (dish is null) return Results.NotFound();
            (dish.HotAdded, dish.HotNote) = (false, null);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
