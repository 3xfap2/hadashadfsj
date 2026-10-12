using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="OfferPrice">Цена по предложению дня; пусто, если скидки на блюдо нет.</param>
public sealed record GuestDish(Guid DishId, string Name, string? PhotoUrl, string? Category, decimal Price, decimal? OfferPrice, int? OfferPercent);
public sealed record GuestMenu(string Restaurant, IEnumerable<GuestDish> Dishes);

public static class GuestMenuEndpoints
{
    /// <summary>
    /// Меню для гостя: открывается без входа по ссылке или QR-коду на столе. В нём только блюда, которые сейчас можно
    /// приготовить, и предложения дня со скидкой. Остатки, сроки и причины скидок гостю не показываются.
    /// </summary>
    public static void MapGuestMenu(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/public/menu/{restaurant:guid}", async (Guid restaurant, AppDbContext db, Tenant tenant, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var name = await db.Restaurants.Where(r => r.Id == restaurant && r.DisabledAt == null).Select(r => r.Name).FirstOrDefaultAsync(ct);
            if (name is null) return Results.NotFound();
            // Входа нет, поэтому ресторан берётся из адреса: дальше все запросы видят только его данные.
            tenant.Id = restaurant;

            var now = time.GetUtcNow();
            var dishes = await db.LoadDishesAsync(ct);
            var totals = await db.LoadTotalsAsync(now, ct);
            var offers = HotOffers.Rank(dishes, await db.LoadFreshBatchesAsync(now, ct), totals, now,
                    await db.LoadHotRulesAsync(options, ct), await db.LoadConsumptionPerHourAsync(now, options, ct))
                .Where(o => o.Discount is not null)
                .ToDictionary(o => o.DishId, o => o.Discount!);

            return Results.Ok(new GuestMenu(name, dishes
                .Where(d => Availability.Of(d, totals).Status != DishStatus.Stop)
                .OrderBy(d => d.Category).ThenBy(d => d.Name)
                .Select(d => new GuestDish(d.Id, d.Name, d.PhotoUrl, d.Category, d.Price, offers.GetValueOrDefault(d.Id)?.Price, offers.GetValueOrDefault(d.Id)?.Percent))));
        })
        .WithTags("Меню для гостя")
        .Produces<GuestMenu>()
        .AllowAnonymous()
        .RequireRateLimiting(ChatEndpoints.RateLimit);
}
