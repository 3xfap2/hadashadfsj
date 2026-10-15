using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>Списания по причинам, ₽ по закупочной цене партии.</summary>
public sealed record OwnerWriteOffs(decimal Expired, decimal Spoiled, decimal Defective)
{
    public decimal Total => Expired + Spoiled + Defective;
}

/// <param name="Orders">Принятые заказы за период; выручка — по ним же.</param>
/// <param name="StopCost">Сколько выручки недополучено из-за стопов за период.</param>
/// <param name="Stopped">Сколько блюд в стопе сейчас.</param>
/// <param name="ToWriteOff">Партии с истёкшим сроком, которые ещё лежат на складе и ждут списания.</param>
/// <param name="ExpiringSoon">Партии, срок которых истекает в ближайшие сутки.</param>
public sealed record OwnerRestaurantSummary(
    Guid Id, string Name, decimal Revenue, int Orders, OwnerWriteOffs WriteOffs, decimal StopCost, int Stopped, int ToWriteOff, int ExpiringSoon);

/// <param name="Times">Сколько раз продукт списывали за период.</param>
/// <param name="Restaurants">В скольких ресторанах его списывали.</param>
public sealed record OwnerTopProduct(string ProductName, Unit Unit, int Times, decimal Quantity, decimal Value, int Restaurants);

/// <param name="From">Начало периода: полночь первого дня по часам ресторанов.</param>
public sealed record OwnerSummary(
    int Days, DateTimeOffset From, IEnumerable<OwnerRestaurantSummary> Restaurants, OwnerRestaurantSummary Total, IEnumerable<OwnerTopProduct> TopWrittenOff);

/// <param name="Owner">Это сам владелец: его запись меняет только он сам.</param>
public sealed record OwnerStaffRow(Guid RestaurantId, string Restaurant, Guid Id, string Login, string Title, Role Role, bool Owner);

public sealed record OwnerSuppliers(int Days, string Formula, decimal Delivered, IEnumerable<SupplierRatingRow> Suppliers);

/// <summary>
/// Кабинет владельца — отдельное приложение /owner/: сводка, сотрудники и поставщики сразу по всем его ресторанам.
/// Токен по-прежнему несёт один ресторан, поэтому данные других ресторанов читаются в обход фильтра ресторана —
/// но только явным условием «ресторан из списка ресторанов владельца».
/// </summary>
public static class OwnerCabinet
{
    public const string NotOwner =
        "Кабинет владельца открыт только владельцу ресторанов — тому, кто добавил себе ресторан в разделе «Ресторан» → «Мои рестораны».";

    public static void MapOwnerCabinet(this RouteGroupBuilder api)
    {
        var owner = api.MapGroup("/owner").WithTags("Кабинет владельца").RequireRoles(Access.Staff).AddEndpointFilter(OwnerOnly);

        owner.MapGet("/summary", async (ClaimsPrincipal principal, AppDbContext db, Tenant tenant, TimeProvider time, StockOptions options, CancellationToken ct, int days = 7) =>
        {
            days = Math.Clamp(days, 1, 90);
            var now = time.GetUtcNow();
            var zone = TimeSpan.FromHours(options.UtcOffsetHours);
            var since = new DateTimeOffset(now.ToOffset(zone).Date.AddDays(1 - days), zone).ToUniversalTime();
            var owned = await OwnedListAsync(db, principal, ct);
            var ids = owned.Select(r => r.Id).ToList();

            var orders = (await db.Orders.IgnoreQueryFilters().AsNoTracking().Include(o => o.Items)
                    .Where(o => ids.Contains(o.RestaurantId) && o.Status == OrderStatus.Accepted && o.CreatedAt >= since)
                    .ToListAsync(ct))
                .ToLookup(o => o.RestaurantId);
            // Списание оценивается по цене партии, из которой списано, — как в аналитике ресторана.
            var writeOffs = await (
                    from m in db.Movements.IgnoreQueryFilters()
                    join b in db.Batches.IgnoreQueryFilters() on m.BatchId equals b.Id
                    join p in db.Products.IgnoreQueryFilters() on m.ProductId equals p.Id
                    where ids.Contains(m.RestaurantId) && m.Type == MovementType.WriteOff && m.At >= since
                    select new { m.RestaurantId, m.Note, m.Quantity, Value = m.Quantity * b.UnitCost, p.Name, p.Unit })
                .ToListAsync(ct);
            var byRestaurant = writeOffs.ToLookup(w => w.RestaurantId);
            var batches = (await db.Batches.IgnoreQueryFilters()
                    .Where(b => ids.Contains(b.RestaurantId) && b.Remaining > 0 && b.ExpiresAt <= now.AddHours(24))
                    .GroupBy(b => b.RestaurantId)
                    .Select(g => new { g.Key, Expired = g.Count(b => b.ExpiresAt <= now), Soon = g.Count(b => b.ExpiresAt > now) })
                    .ToListAsync(ct))
                .ToDictionary(b => b.Key);

            // Стоп-лист и цена стопа считаются доменными расчётами ресторана — в его собственном пространстве данных.
            var stops = new Dictionary<Guid, (int Stopped, decimal Cost)>();
            var current = tenant.Id;
            try
            {
                foreach (var restaurant in owned)
                {
                    tenant.Id = restaurant.Id;
                    var dishes = await db.LoadDishesAsync(ct);
                    var totals = await db.LoadTotalsAsync(now, ct);
                    // Скорость продаж блюда — не меньше чем за две недели: по одному дню её не оценить (как в отчёте за смену).
                    var cost = await InsightsEndpoints.StopCostAsync(db, options, now.AddDays(-Math.Max(days, 14)), now, ct, since);
                    stops[restaurant.Id] = (dishes.Count(d => Availability.Of(d, totals).Status == DishStatus.Stop), cost.Sum(r => r.LostRevenue));
                }
            }
            finally
            {
                tenant.Id = current;
            }

            var rows = owned.Select(r =>
            {
                var losses = byRestaurant[r.Id].ToList();
                decimal Reason(Func<string?, bool> match) => losses.Where(w => match(w.Note)).Sum(w => w.Value);
                return new OwnerRestaurantSummary(
                    r.Id, r.Name,
                    orders[r.Id].Sum(OrderEndpoints.Total), orders[r.Id].Count(),
                    new OwnerWriteOffs(
                        // Всё, что не брак и не порча, — списание по сроку: вручную или автоматически (как в отчёте по поставщикам).
                        Reason(n => n != WriteOffService.DefectiveNote && n != WriteOffService.SpoiledNote),
                        Reason(n => n == WriteOffService.SpoiledNote),
                        Reason(n => n == WriteOffService.DefectiveNote)),
                    stops[r.Id].Cost, stops[r.Id].Stopped,
                    batches.GetValueOrDefault(r.Id)?.Expired ?? 0, batches.GetValueOrDefault(r.Id)?.Soon ?? 0);
            }).ToList();

            var total = new OwnerRestaurantSummary(
                Guid.Empty, "Все рестораны", rows.Sum(r => r.Revenue), rows.Sum(r => r.Orders),
                new OwnerWriteOffs(rows.Sum(r => r.WriteOffs.Expired), rows.Sum(r => r.WriteOffs.Spoiled), rows.Sum(r => r.WriteOffs.Defective)),
                rows.Sum(r => r.StopCost), rows.Sum(r => r.Stopped), rows.Sum(r => r.ToWriteOff), rows.Sum(r => r.ExpiringSoon));

            // У каждого ресторана свой справочник: один и тот же продукт узнаётся по названию и единице.
            var top = writeOffs
                .GroupBy(w => (w.Name, w.Unit))
                .Select(g => new OwnerTopProduct(g.Key.Name, g.Key.Unit, g.Count(), g.Sum(w => w.Quantity), Math.Round(g.Sum(w => w.Value)), g.Select(w => w.RestaurantId).Distinct().Count()))
                .OrderByDescending(p => p.Value).ThenByDescending(p => p.Times).ThenBy(p => p.ProductName)
                .Take(5)
                .ToList();

            return new OwnerSummary(days, since, rows, total, top);
        });

        owner.MapGet("/staff", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var owned = (await OwnedListAsync(db, principal, ct)).ToDictionary(r => r.Id, r => r.Name);
            var ids = owned.Keys.ToList();
            var login = principal.Identity!.Name;
            return (await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => ids.Contains(u.RestaurantId))
                    .Select(u => new { u.RestaurantId, u.Id, u.Login, u.Title, u.Role })
                    .ToListAsync(ct))
                .Select(u => new OwnerStaffRow(u.RestaurantId, owned[u.RestaurantId], u.Id, u.Login, u.Title, u.Role, u.Login == login))
                .OrderBy(u => ids.IndexOf(u.RestaurantId)).ThenBy(u => u.Login)
                .ToList();
        });

        // Сотрудники любого своего ресторана — теми же действиями и с теми же проверками, что и в кабинете ресторана.
        var staff = owner.MapGroup("/restaurants/{restaurantId:guid}/users");
        staff.MapPost("", (Guid restaurantId, StaffRequest req, ClaimsPrincipal principal, AppDbContext db, Tenant tenant, CancellationToken ct) =>
            InRestaurantAsync(restaurantId, principal, db, tenant, ct, () => StaffEndpoints.CreateAsync(req, db, ct)));
        staff.MapPut("{id:guid}", (Guid restaurantId, Guid id, StaffUpdate req, ClaimsPrincipal principal, AppDbContext db, Tenant tenant, AuthOptions options, CancellationToken ct) =>
            InRestaurantAsync(restaurantId, principal, db, tenant, ct, () => StaffEndpoints.UpdateAsync(id, req, principal, db, options, ct)));
        staff.MapDelete("{id:guid}", (Guid restaurantId, Guid id, ClaimsPrincipal principal, AppDbContext db, Tenant tenant, AuthOptions options, CancellationToken ct) =>
            InRestaurantAsync(restaurantId, principal, db, tenant, ct, () => StaffEndpoints.DeleteAsync(id, principal, db, options, ct)));

        // Рейтинг поставщиков по всем ресторанам: отчёт по поставщикам, собранный по партиям всех ресторанов владельца.
        owner.MapGet("/suppliers", async (ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken ct, int days = 30) =>
        {
            days = Math.Clamp(days, 1, 90);
            var now = time.GetUtcNow();
            var ids = (await OwnedListAsync(db, principal, ct)).Select(r => r.Id).ToList();
            var flows = await InsightsEndpoints.SupplierFlowsAsync(
                db.Batches.IgnoreQueryFilters().Where(b => ids.Contains(b.RestaurantId)),
                db.Movements.IgnoreQueryFilters().Where(m => ids.Contains(m.RestaurantId)),
                now.AddDays(-days), ct);
            var rows = SupplierRating.Build(flows, now);
            return new OwnerSuppliers(days, SupplierRating.Formula, rows.Sum(r => r.Delivered), rows);
        });
    }

    /// <summary>Владелец — тот, кто добавил себе хотя бы один ресторан сам.</summary>
    public static Task<bool> IsOwnerAsync(AppDbContext db, string? login, CancellationToken ct) =>
        db.Restaurants.AnyAsync(r => r.OwnerLogin == login, ct);

    private static async ValueTask<object?> OwnerOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!await IsOwnerAsync(http.RequestServices.GetRequiredService<AppDbContext>(), http.User.Identity?.Name, http.RequestAborted))
            return Results.Problem(NotOwner, statusCode: StatusCodes.Status403Forbidden);
        return await next(context);
    }

    /// <summary>Рестораны владельца в порядке добавления: те, где записан его логин (в том числе первый, открытый командой продукта).</summary>
    internal static async Task<List<(Guid Id, string Name)>> OwnedListAsync(AppDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        (await OwnerEndpoints.InOrder(OwnerEndpoints.OwnerOf(db, principal), db, principal).Select(r => new { r.Id, r.Name }).ToListAsync(ct))
            .Select(r => (r.Id, r.Name)).ToList();

    /// <summary>
    /// Действие в выбранном ресторане владельца: чужой ресторан — 404, как будто его нет. Дальше запрос работает
    /// в пространстве данных выбранного ресторана: так действуют те же проверки, а событие об изменении уходит его кабинетам.
    /// </summary>
    internal static async Task<IResult> InRestaurantAsync(
        Guid restaurantId, ClaimsPrincipal principal, AppDbContext db, Tenant tenant, CancellationToken ct, Func<Task<IResult>> action)
    {
        if (!await OwnerEndpoints.OwnerOf(db, principal).AnyAsync(r => r.Id == restaurantId, ct)) return Results.NotFound();
        tenant.Id = restaurantId;
        return await action();
    }
}
