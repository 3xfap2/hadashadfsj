using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Place">Стол или бар из списка мест ресторана; можно не указывать.</param>
public sealed record OrderRequest(List<OrderLine>? Items, string? Place = null);

public sealed record OrderItemResponse(Guid DishId, string DishName, int Quantity, int DiscountPercent);
public sealed record CloseTableRequest(string? Place);
public sealed record CloseTableResponse(string Place, int Orders, decimal Total);
public sealed record ShortageResponse(Guid ProductId, string ProductName, Unit Unit, decimal Required, decimal Available);
public sealed record OrderWriteOff(string ProductName, Unit Unit, string SupplyNumber, decimal Quantity);

/// <param name="WriteOffs">Из каких партий списаны продукты; заполняется в ответе на создание заказа.</param>
public sealed record OrderResponse(
    Guid Id, int Number, DateTimeOffset CreatedAt, OrderStatus Status, string Place, decimal Total,
    IEnumerable<OrderItemResponse> Items, IEnumerable<ShortageResponse> Shortages,
    IEnumerable<OrderWriteOff> WriteOffs, DateTimeOffset? ClosedAt);

public static class OrderEndpoints
{
    public static void MapOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Заказы");

        orders.MapGet("", async (AppDbContext db, CancellationToken ct, int take = 50) =>
            (await Detailed(db).OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct))
            .Select(ToResponse));

        // Закрыть стол: гость расплатился, открытые заказы стола уходят в историю, стол снова свободен.
        orders.MapPost("/close", async (CloseTableRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var place = req.Place?.Trim() ?? "";
            var open = await Detailed(db).Where(o => o.Place == place && o.Status == OrderStatus.Accepted && o.ClosedAt == null).ToListAsync(ct);
            if (place == "" || open.Count == 0) return Results.NotFound();

            var now = time.GetUtcNow();
            foreach (var order in open) order.ClosedAt = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new CloseTableResponse(place, open.Count, open.Sum(Total)));
        })
        .RequireRoles(Access.Orders);

        orders.MapPost("", async (OrderRequest req, OrderService service, AppDbContext db, RestaurantOptions restaurant, ClaimsPrincipal user,
            TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var items = req.Items ?? [];
            var place = req.Place?.Trim() ?? "";
            var errors = new Errors()
                .Check(place == "" || restaurant.Places.Contains(place), "place", "Такого места в ресторане нет.")
                .Check(items.Count > 0, "items", "В заказе должно быть хотя бы одно блюдо.")
                .Check(items.All(i => i.Quantity is > 0 and <= 1000), "items", "Количество порций — от 1 до 1000.");

            var ids = items.Select(i => i.DishId).Distinct().ToArray();
            var known = await db.Dishes.CountAsync(d => ids.Contains(d.Id), ct);
            if (errors.Check(known == ids.Length, "items", "В заказе есть неизвестное блюдо.").Any) return errors.ToResult();

            var paused = await db.Dishes.Where(d => ids.Contains(d.Id) && d.ManuallyStopped).Select(d => d.Name).ToListAsync(ct);
            if (paused.Count > 0)
                return errors.Add("items", $"Снято с продажи: {string.Join(", ", paused)}. Уберите блюдо из заказа.").ToResult();

            // Размер скидки задаёт сервер: касса только просит её применить. Если горячее предложение
            // уже не действует, блюдо продаётся по полной цене.
            var discounts = new Dictionary<Guid, int>();
            var asked = items.Where(i => i.Discount).Select(i => i.DishId).ToHashSet();
            if (asked.Count > 0)
            {
                var now = time.GetUtcNow();
                var offers = HotOffers.Rank(
                    await db.LoadDishesAsync(ct), await db.LoadFreshBatchesAsync(now, ct), await db.LoadTotalsAsync(now, ct),
                    now, await db.LoadHotRulesAsync(options, ct), await db.LoadConsumptionPerHourAsync(now, options, ct));
                discounts = offers.Where(o => o.Discount is not null && asked.Contains(o.DishId)).ToDictionary(o => o.DishId, o => o.Discount!.Percent);
            }

            var placed = await service.PlaceAsync(items, place, user.Identity?.Name ?? "", ct, discounts);
            var writeOffs = await (
                from m in db.Movements
                where m.OrderId == placed.Id
                join b in db.Batches on m.BatchId equals b.Id
                orderby m.Id
                select new OrderWriteOff(m.Product!.Name, m.Product.Unit, b.Supply!.Number, m.Quantity)).ToListAsync(ct);
            var response = ToResponse(await Detailed(db).AsNoTracking().FirstAsync(o => o.Id == placed.Id, ct), writeOffs);

            // Заказ с нехваткой не создан: 409 и перечень того, чего не хватило.
            return placed.Status == OrderStatus.Accepted
                ? Results.Created($"/api/orders/{placed.Id}", response)
                : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
        })
        .Produces<OrderResponse>(StatusCodes.Status201Created)
        .Produces<OrderResponse>(StatusCodes.Status409Conflict)
        .RequireRoles(Access.Orders);
    }

    private static IQueryable<Order> Detailed(AppDbContext db) => db.Orders
        .Include(o => o.Items).ThenInclude(i => i.Dish)
        .Include(o => o.Shortages).ThenInclude(s => s.Product);

    private static OrderResponse ToResponse(Order o) => ToResponse(o, []);

    private static OrderResponse ToResponse(Order o, IEnumerable<OrderWriteOff> writeOffs) => new(
        o.Id, o.Number, o.CreatedAt, o.Status, o.Place, Total(o),
        o.Items.Select(i => new OrderItemResponse(i.DishId, i.Dish!.Name, i.Quantity, i.DiscountPercent)),
        o.Shortages.Select(s => new ShortageResponse(s.ProductId, s.Product!.Name, s.Product.Unit, s.Required, s.Available)),
        writeOffs, o.ClosedAt);

    /// <summary>Сумма заказа; цена со скидкой округляется до рубля, как её показывает горячее предложение.</summary>
    private static decimal Total(Order o) => o.Items.Sum(i => Math.Round(i.Dish!.Price * (100 - i.DiscountPercent) / 100) * i.Quantity);
}
