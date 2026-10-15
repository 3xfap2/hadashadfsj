using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Current">В этом ресторане владелец работает сейчас.</param>
/// <param name="Stopped">Сколько блюд в стопе.</param>
/// <param name="AtRiskValue">На сколько рублей продуктов истекает в пределах горизонта горячих предложений.</param>
public sealed record MyRestaurant(Guid Id, string Name, bool Current, int Stopped, decimal AtRiskValue);
public sealed record MyRestaurantRequest(string? Name);

/// <summary>
/// Рестораны одного владельца. Управляющий может добавить себе ещё один ресторан и переходить между ними:
/// при переходе выдаётся новый токен с другим рестораном, поэтому данные по-прежнему не смешиваются —
/// каждый запрос видит только тот ресторан, который записан в токене.
/// </summary>
public static class OwnerEndpoints
{
    /// <summary>Сколько ресторанов владелец может добавить сам; больше — через команду продукта.</summary>
    public const int MaxOwned = 10;

    public static void MapOwner(this RouteGroupBuilder api)
    {
        var mine = api.MapGroup("/my/restaurants").WithTags("Рестораны владельца").RequireRoles(Access.Staff);

        mine.MapGet("", async (ClaimsPrincipal principal, AppDbContext db, Tenant tenant, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var current = tenant.Id;
            var now = time.GetUtcNow();
            var rows = new List<MyRestaurant>();
            try
            {
                foreach (var restaurant in await InOrder(OwnedAsync(db, principal), db, principal).ToListAsync(ct))
                {
                    // Сводка каждого ресторана считается в его собственном пространстве данных.
                    tenant.Id = restaurant.Id;
                    var dishes = await db.LoadDishesAsync(ct);
                    var totals = await db.LoadTotalsAsync(now, ct);
                    var plan = HotOffers.Plan(dishes, await db.LoadFreshBatchesAsync(now, ct), totals, now, await db.LoadHotRulesAsync(options, ct));
                    rows.Add(new MyRestaurant(restaurant.Id, restaurant.Name, restaurant.Id == current,
                        dishes.Count(d => Availability.Of(d, totals).Status == DishStatus.Stop), plan.AtRiskValue ?? 0));
                }
            }
            finally
            {
                tenant.Id = current;
            }
            return rows;
        });

        mine.MapPost("", async (MyRestaurantRequest req, ClaimsPrincipal principal, AppDbContext db, RestaurantOptions defaults, TimeProvider time, CancellationToken ct) =>
        {
            var login = principal.Identity!.Name!;
            var home = await db.Users.IgnoreQueryFilters().Where(u => u.Login == login).Select(u => u.RestaurantId).FirstAsync(ct);
            // Вход в демо-ресторан открыт всем: если разрешить здесь создавать рестораны, их наплодит любой посетитель.
            if (home == Restaurant.DemoId)
                return Results.Problem("В демо-ресторане добавить свой ресторан нельзя. Оставьте заявку на сайте — откроем вам кабинет.", statusCode: StatusCodes.Status403Forbidden);

            var name = (req.Name ?? "").Trim();
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "name", "Укажите название ресторана — до 120 символов.")
                .Check(await db.Restaurants.CountAsync(r => r.OwnerLogin == login && r.Id != home, ct) < MaxOwned, "name", $"Самостоятельно можно добавить до {MaxOwned} ресторанов. Напишите в поддержку.");
            if (errors.Any) return errors.ToResult();

            var restaurant = new Restaurant
            {
                Id = Guid.NewGuid(), Name = name, CreatedAt = time.GetUtcNow(), OwnerLogin = login,
                ShiftStart = defaults.ShiftStart, ShiftEnd = defaults.ShiftEnd,
            };
            db.Restaurants.Add(restaurant);
            await db.SaveChangesAsync(ct);
            // Ресторан, открытый командой продукта, становится рестораном владельца, только если владельца у него ещё нет:
            // второй управляющий, добавив себе ресторан, чужой переписки и кабинета не получает.
            await db.Restaurants.Where(r => r.Id == home && r.OwnerLogin == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerLogin, login), ct);
            return Results.Created($"/api/my/restaurants/{restaurant.Id}", new MyRestaurant(restaurant.Id, restaurant.Name, false, 0, 0));
        });

        // Перейти в другой свой ресторан: новый токен с тем же сотрудником, но другим рестораном.
        mine.MapPost("{id:guid}/enter", async (Guid id, ClaimsPrincipal principal, AppDbContext db, AuthOptions options, TimeProvider time, CancellationToken ct) =>
        {
            if (!await OwnedAsync(db, principal).AnyAsync(r => r.Id == id, ct)) return Results.NotFound();
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Login == principal.Identity!.Name, ct);
            return Results.Ok(AuthEndpoints.Issue(user, options, time, id));
        }).Produces<LoginResponse>();
    }

    /// <summary>Рестораны сотрудника: тот, где заведена его учётная запись, и те, что он добавил сам. Отключённые не показываются.</summary>
    internal static IQueryable<Restaurant> OwnedAsync(AppDbContext db, ClaimsPrincipal principal)
    {
        var login = principal.Identity!.Name;
        return db.Restaurants.Where(r => r.DisabledAt == null
            && (r.OwnerLogin == login || db.Users.IgnoreQueryFilters().Any(u => u.Login == login && u.RestaurantId == r.Id)));
    }

    /// <summary>
    /// Рестораны в порядке добавления; при равном времени первым — тот, где заведена учётная запись
    /// (ресторан, открытый командой продукта, и добавленный сразу за ним иначе меняются местами).
    /// </summary>
    internal static IOrderedQueryable<Restaurant> InOrder(IQueryable<Restaurant> restaurants, AppDbContext db, ClaimsPrincipal principal)
    {
        var login = principal.Identity!.Name;
        return restaurants.OrderBy(r => r.CreatedAt)
            .ThenByDescending(r => db.Users.IgnoreQueryFilters().Any(u => u.Login == login && u.RestaurantId == r.Id))
            .ThenBy(r => r.Id);
    }

    /// <summary>
    /// Рестораны, где сотрудник — владелец: его логин записан в ресторане. Только они открыты в кабинете владельца и в переписке;
    /// место работы владельцем не делает. Отключённые не показываются.
    /// </summary>
    internal static IQueryable<Restaurant> OwnerOf(AppDbContext db, ClaimsPrincipal principal)
    {
        var login = principal.Identity!.Name;
        return db.Restaurants.Where(r => r.DisabledAt == null && r.OwnerLogin == login);
    }
}
