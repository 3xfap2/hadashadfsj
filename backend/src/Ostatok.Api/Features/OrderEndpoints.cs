using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Place">Стол или бар из списка мест ресторана; можно не указывать.</param>
public sealed record OrderRequest(List<OrderLine>? Items, string? Place = null);

public sealed record OrderItemResponse(Guid DishId, string DishName, int Quantity);
public sealed record ShortageResponse(Guid ProductId, string ProductName, Unit Unit, decimal Required, decimal Available);
public sealed record OrderWriteOff(string ProductName, Unit Unit, string SupplyNumber, decimal Quantity);

/// <param name="WriteOffs">Из каких партий списаны продукты; заполняется в ответе на создание заказа.</param>
public sealed record OrderResponse(
    Guid Id, int Number, DateTimeOffset CreatedAt, OrderStatus Status, string Place, decimal Total,
    IEnumerable<OrderItemResponse> Items, IEnumerable<ShortageResponse> Shortages,
    IEnumerable<OrderWriteOff> WriteOffs);

public static class OrderEndpoints
{
    public static void MapOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Заказы");

        orders.MapGet("", async (AppDbContext db, CancellationToken ct, int take = 50) =>
            (await Detailed(db).OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct))
            .Select(ToResponse));

        orders.MapPost("", async (OrderRequest req, OrderService service, AppDbContext db, RestaurantOptions restaurant, CancellationToken ct) =>
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

            var placed = await service.PlaceAsync(items, place, ct);
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
        .Produces<OrderResponse>(StatusCodes.Status409Conflict);
    }

    private static IQueryable<Order> Detailed(AppDbContext db) => db.Orders
        .Include(o => o.Items).ThenInclude(i => i.Dish)
        .Include(o => o.Shortages).ThenInclude(s => s.Product);

    private static OrderResponse ToResponse(Order o) => ToResponse(o, []);

    private static OrderResponse ToResponse(Order o, IEnumerable<OrderWriteOff> writeOffs) => new(
        o.Id, o.Number, o.CreatedAt, o.Status, o.Place, o.Items.Sum(i => i.Dish!.Price * i.Quantity),
        o.Items.Select(i => new OrderItemResponse(i.DishId, i.Dish!.Name, i.Quantity)),
        o.Shortages.Select(s => new ShortageResponse(s.ProductId, s.Product!.Name, s.Product.Unit, s.Required, s.Available)),
        writeOffs);
}
