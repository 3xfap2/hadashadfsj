using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Place">Стол или бар из списка мест ресторана; можно не указывать.</param>
/// <param name="Id">Идентификатор заказа, который придумал клиент; повтор с тем же Id вернёт прежний заказ и ничего не спишет.</param>
public sealed record OrderRequest(List<OrderLine>? Items, string? Place = null, Guid? Id = null);

/// <param name="DiscountQuantity">На сколько порций из Quantity действует скидка.</param>
public sealed record OrderItemResponse(Guid DishId, string DishName, int Quantity, int DiscountPercent, int DiscountQuantity, decimal Price);
public sealed record CloseTableRequest(string? Place);
public sealed record CloseTableResponse(string Place, int Orders, decimal Total);
/// <param name="Ready">Кухня отдала все открытые заказы стола.</param>
public sealed record OpenTable(string Place, decimal Total, DateTimeOffset Last, bool Ready);

/// <summary>Итоги с начала дня и столы с открытыми заказами: считаются по всем заказам, а не по последней странице списка.</summary>
public sealed record OrdersSummary(int Orders, int Rejected, decimal Revenue, IEnumerable<OpenTable> Tables);
public sealed record ShortageResponse(Guid ProductId, string ProductName, Unit Unit, decimal Required, decimal Available);
public sealed record OrderWriteOff(string ProductName, Unit Unit, string SupplyNumber, decimal Quantity);

/// <param name="WriteOffs">Из каких партий списаны продукты; заполняется в ответе на создание заказа.</param>
/// <param name="Source">Внешняя система, приславшая заказ (название её ключа); пусто — заказ оформлен в кабинете.</param>
/// <param name="Delivery">Заказ на доставку.</param>
public sealed record OrderResponse(
    Guid Id, int Number, DateTimeOffset CreatedAt, OrderStatus Status, string Place, decimal Total,
    IEnumerable<OrderItemResponse> Items, IEnumerable<ShortageResponse> Shortages,
    IEnumerable<OrderWriteOff> WriteOffs, DateTimeOffset? ClosedAt, DateTimeOffset? ReadyAt,
    string? Source = null, bool Delivery = false);

/// <param name="Id">Номер заказа во внешней системе, до 64 символов. Повтор с тем же номером от того же ключа вернёт прежний заказ и ничего не спишет.</param>
/// <param name="Items">Блюда (dishId из GET /api/integration/menu) и порции. Скидки горячих предложений к внешним заказам не применяются.</param>
/// <param name="Place">Стол или бар ресторана; пусто — без места (самовывоз, касса).</param>
/// <param name="Delivery">Заказ на доставку: место не указывается.</param>
public sealed record ExternalOrderRequest(string? Id, List<OrderLine>? Items, string? Place = null, bool Delivery = false);

public sealed record ExternalOrderItem(Guid DishId, string DishName, int Quantity, decimal Price);

/// <summary>Квитанция внешней системе: без складских данных ресторана (партий, остатков, закупочных цен).</summary>
/// <param name="Unavailable">Блюда, которые сейчас не приготовить; заполнено, если заказ не принят.</param>
/// <param name="Detail">Причина отказа текстом; пусто у принятого заказа.</param>
public sealed record ExternalOrderResponse(
    Guid Id, string ExternalId, int Number, OrderStatus Status, string Place, bool Delivery, string Source, decimal Total,
    DateTimeOffset CreatedAt, IEnumerable<ExternalOrderItem> Items, IEnumerable<string> Unavailable, string? Detail);

/// <summary>Экран кухни: очередь заказов от старых к новым и несколько последних отданных.</summary>
public sealed record KitchenBoard(IEnumerable<OrderResponse> Queue, IEnumerable<OrderResponse> Ready);

public static class OrderEndpoints
{
    /// <summary>Сколько часов заказ остаётся на экране кухни: вчерашние и забытые заказы очередь не занимают.</summary>
    private const int KitchenHours = 6;

    public static void MapOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Заказы");

        orders.MapGet("", async (AppDbContext db, CancellationToken ct, int take = 50) =>
            (await Detailed(db).OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct))
            .Select(ToResponse));

        orders.MapGet("/summary", async (AppDbContext db, TimeProvider time, CancellationToken ct, DateTimeOffset? since = null) =>
        {
            var now = time.GetUtcNow();
            // База принимает время только в UTC; клиент может прислать его со своим часовым поясом.
            var from = since?.ToUniversalTime() ?? now.AddHours(-24);
            var day = await db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.CreatedAt >= from).ToListAsync(ct);
            var accepted = day.Where(o => o.Status == OrderStatus.Accepted).ToList();
            var tables = (await OpenOrders(db, now).AsNoTracking().Include(o => o.Items).ToListAsync(ct))
                .GroupBy(o => o.Place)
                .Select(g => new OpenTable(g.Key, g.Sum(Total), g.Max(o => o.CreatedAt), g.All(o => o.ReadyAt != null)));
            return new OrdersSummary(day.Count, day.Count - accepted.Count, accepted.Sum(Total), tables);
        });

        orders.MapGet("/kitchen", async (AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow().AddHours(-KitchenHours);
            var recent = Detailed(db).AsNoTracking().Where(o => o.Status == OrderStatus.Accepted && o.CreatedAt >= since);
            return new KitchenBoard(
                (await recent.Where(o => o.ReadyAt == null).OrderBy(o => o.CreatedAt).Take(100).ToListAsync(ct)).Select(ToResponse),
                (await recent.Where(o => o.ReadyAt != null).OrderByDescending(o => o.ReadyAt).Take(5).ToListAsync(ct)).Select(ToResponse));
        })
        .RequireRoles(Access.Kitchen);

        // Повар отдал заказ: он уходит из очереди, официант видит отметку «готов».
        orders.MapPost("{id:guid}/ready", async (Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id && o.Status == OrderStatus.Accepted, ct);
            if (order is null) return Results.NotFound();
            // Повторная отметка время не меняет: два повара могут нажать одновременно.
            order.ReadyAt ??= time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireRoles(Access.Kitchen);

        // Закрыть стол: гость расплатился, открытые заказы стола уходят в историю, стол снова свободен.
        orders.MapPost("/close", async (CloseTableRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var place = req.Place?.Trim() ?? "";
            var now = time.GetUtcNow();
            // Закрываются те же заказы, что показаны на столе в зале: открытые за последние сутки.
            var open = await OpenOrders(db, now).Include(o => o.Items).Where(o => o.Place == place).ToListAsync(ct);
            if (place == "" || open.Count == 0) return Results.NotFound();

            foreach (var order in open) order.ClosedAt = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new CloseTableResponse(place, open.Count, open.Sum(Total)));
        })
        .RequireRoles(Access.Orders);

        orders.MapPost("", async (OrderRequest req, OrderService service, AppDbContext db, ClaimsPrincipal user,
            TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            // Повтор уже выполненного запроса: отвечаем тем же, что и в первый раз, не проверяя заказ заново.
            if (req.Id is not null && await db.Orders.AnyAsync(o => o.Id == req.Id, ct)) return await AnswerAsync(req.Id.Value);

            var items = req.Items ?? [];
            var place = req.Place?.Trim() ?? "";
            var places = (await db.CurrentRestaurantAsync(ct)).Places();
            var errors = new Errors()
                .Check(place == "" || places.Contains(place), "place", "Такого места в ресторане нет.")
                .Check(items.Count > 0, "items", "В заказе должно быть хотя бы одно блюдо.")
                .Check(items.Count <= 200, "items", "В заказе не больше 200 строк.")
                .Check(items.All(i => i.Quantity is > 0 and <= 1000), "items", "Количество порций — от 1 до 1000.")
                .Check(items.GroupBy(i => i.DishId).All(g => g.Sum(i => (long)i.Quantity) <= 1000), "items", "Одного блюда в заказе — не больше 1000 порций.");

            var ids = items.Select(i => i.DishId).Distinct().ToArray();
            var known = await db.Dishes.CountAsync(d => ids.Contains(d.Id), ct);
            if (errors.Check(known == ids.Length, "items", "В заказе есть неизвестное блюдо.").Any) return errors.ToResult();

            var paused = await db.Dishes.Where(d => ids.Contains(d.Id) && d.ManuallyStopped).Select(d => d.Name).ToListAsync(ct);
            if (paused.Count > 0)
                return errors.Add("items", $"Снято с продажи: {string.Join(", ", paused)}. Уберите блюдо из заказа.").ToResult();

            // Размер скидки задаёт сервер: касса только просит её применить. Если горячее предложение
            // уже не действует, блюдо продаётся по полной цене.
            // Скидка действует только на столько порций, сколько нужно продать, чтобы истекающий продукт не пропал:
            // остальные порции готовятся уже из свежей партии и идут по полной цене.
            var discounts = new Dictionary<Guid, (int Percent, int Portions)>();
            var asked = items.Where(i => i.Discount).GroupBy(i => i.DishId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
            if (asked.Count > 0)
            {
                var now = time.GetUtcNow();
                var offers = HotOffers.Rank(
                    await db.LoadDishesAsync(ct), await db.LoadFreshBatchesAsync(now, ct), await db.LoadTotalsAsync(now, ct),
                    now, await db.LoadHotRulesAsync(options, ct), await db.LoadConsumptionPerHourAsync(now, options, ct));
                discounts = offers.Where(o => o.Discount is not null && asked.ContainsKey(o.DishId))
                    .ToDictionary(o => o.DishId, o => (o.Discount!.Percent, Math.Min(asked[o.DishId], o.Portions)));
            }

            var placed = await service.PlaceAsync(items, place, user.Identity?.Name ?? "", ct, discounts, req.Id);
            return await AnswerAsync(placed.Id);

            async Task<IResult> AnswerAsync(Guid id)
            {
                var writeOffs = await (
                    from m in db.Movements
                    where m.OrderId == id
                    join b in db.Batches on m.BatchId equals b.Id
                    orderby m.Id
                    select new OrderWriteOff(m.Product!.Name, m.Product.Unit, b.Supply!.Number, m.Quantity)).ToListAsync(ct);
                var order = await Detailed(db).AsNoTracking().FirstAsync(o => o.Id == id, ct);
                var response = ToResponse(order, writeOffs);

                // Заказ с нехваткой не принят: 409 и перечень того, чего не хватило.
                return order.Status == OrderStatus.Accepted
                    ? Results.Created($"/api/orders/{id}", response)
                    : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
            }
        })
        .Produces<OrderResponse>(StatusCodes.Status201Created)
        .Produces<OrderResponse>(StatusCodes.Status409Conflict)
        .RequireRoles(Access.Orders);

        // Заказ извне — с сайта кафе или кассы — по ключу с правом orders. Идёт тем же OrderService, что и заказ официанта:
        // те же блокировки партий и тот же отказ при нехватке. Исполнитель в журнале — название ключа.
        orders.MapPost("/external", async (ExternalOrderRequest req, HttpContext http, OrderService service, AppDbContext db, CancellationToken ct) =>
        {
            var key = IntegrationKey.Current(http);
            var externalId = req.Id?.Trim() ?? "";
            var items = req.Items ?? [];
            var place = req.Place?.Trim() ?? "";
            var places = (await db.CurrentRestaurantAsync(ct)).Places();
            var errors = new Errors()
                .Check(externalId is { Length: > 0 and <= 64 }, "id", "Укажите номер заказа во внешней системе — до 64 символов.")
                .Check(!req.Delivery || place == "", "place", "У заказа на доставку нет места в зале.")
                .Check(place == "" || places.Contains(place), "place", "Такого места в ресторане нет.")
                .Check(items.Count > 0, "items", "В заказе должно быть хотя бы одно блюдо.")
                .Check(items.Count <= 200, "items", "В заказе не больше 200 строк.")
                .Check(items.All(i => i.Quantity is > 0 and <= 1000), "items", "Количество порций — от 1 до 1000.")
                .Check(items.GroupBy(i => i.DishId).All(g => g.Sum(i => (long)i.Quantity) <= 1000), "items", "Одного блюда в заказе — не больше 1000 порций.");
            if (errors.Any) return errors.ToResult();

            // Идентификатор заказа выводится из ключа и номера во внешней системе: повтор того же запроса попадает в тот же заказ,
            // а одинаковые номера у сайта и кассы не путаются.
            var id = ExternalOrderId(key.Id, externalId);
            if (await db.Orders.AnyAsync(o => o.Id == id, ct)) return await AnswerAsync(id);

            var ids = items.Select(i => i.DishId).Distinct().ToArray();
            var known = await db.Dishes.CountAsync(d => ids.Contains(d.Id), ct);
            if (errors.Check(known == ids.Length, "items", "В заказе есть неизвестное блюдо: dishId берётся из GET /api/integration/menu.").Any) return errors.ToResult();

            var paused = await db.Dishes.Where(d => ids.Contains(d.Id) && d.ManuallyStopped).Select(d => d.Name).ToListAsync(ct);
            if (paused.Count > 0)
                return Results.Problem($"Снято с продажи: {string.Join(", ", paused)}. Уберите блюдо из заказа.", statusCode: StatusCodes.Status409Conflict,
                    title: "Заказ не принят", extensions: new Dictionary<string, object?> { ["unavailable"] = paused });

            // Источник заказа записывается тем же сохранением, что и сам заказ: OrderService о внешних системах не знает.
            void Mark(object? sender, SavingChangesEventArgs e)
            {
                foreach (var entry in db.ChangeTracker.Entries<Order>().Where(x => x.State == EntityState.Added))
                    (entry.Entity.Source, entry.Entity.ExternalId, entry.Entity.Delivery) = (key.Name, externalId, req.Delivery);
            }
            db.SavingChanges += Mark;
            try
            {
                await service.PlaceAsync(items.Select(i => i with { Discount = false }).ToList(), place, key.Name, ct, id: id);
            }
            finally
            {
                db.SavingChanges -= Mark;
            }
            return await AnswerAsync(id);

            async Task<IResult> AnswerAsync(Guid orderId)
            {
                var order = await Detailed(db).AsNoTracking().Include(o => o.Items).ThenInclude(i => i.Dish).ThenInclude(d => d!.Recipe)
                    .FirstAsync(o => o.Id == orderId, ct);
                var missing = order.Shortages.Select(s => s.ProductId).ToHashSet();
                var unavailable = order.Items.Where(i => i.Dish!.Recipe.Any(r => missing.Contains(r.ProductId))).Select(i => i.Dish!.Name).ToList();
                var accepted = order.Status == OrderStatus.Accepted;
                var response = new ExternalOrderResponse(
                    order.Id, order.ExternalId ?? externalId, order.Number, order.Status, order.Place, order.Delivery, order.Source ?? key.Name, Total(order), order.CreatedAt,
                    order.Items.Select(i => new ExternalOrderItem(i.DishId, i.Dish!.Name, i.Quantity, i.Price)), unavailable,
                    accepted ? null : $"Не хватает продуктов для блюд: {string.Join(", ", unavailable)}. Ничего не списано.");
                return accepted
                    ? Results.Created($"/api/orders/{order.Id}", response)
                    : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
            }
        })
        .RequireKey(IntegrationKey.Orders)
        .Produces<ExternalOrderResponse>(StatusCodes.Status201Created)
        .Produces<ExternalOrderResponse>(StatusCodes.Status409Conflict)
        .WithSummary("Заказ извне: сайт кафе или касса по ключу")
        .WithDescription("Нужен ключ с правом orders в заголовке X-Api-Key. Заказ проходит тот же путь, что и заказ официанта: годные партии блокируются, продукты списываются по FEFO одной транзакцией. 201 — принят и ушёл на кухню; 409 — блюдо снято с продажи или не хватает продуктов (перечень в unavailable, ничего не списано). Повтор с тем же id возвращает прежний заказ.");
    }

    /// <summary>Идентификатор заказа из ключа и номера во внешней системе: первые 16 байт SHA-256.</summary>
    private static Guid ExternalOrderId(Guid key, string externalId) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key:N}:{externalId}")).AsSpan(0, 16));

    /// <summary>Заказы, по которым гость ещё не расплатился. Стол не бывает открыт дольше суток, поэтому старые заказы зал не занимают.</summary>
    public static IQueryable<Order> OpenOrders(AppDbContext db, DateTimeOffset now) => db.Orders
        .Where(o => o.Status == OrderStatus.Accepted && o.ClosedAt == null && o.Place != "" && o.CreatedAt >= now.AddHours(-24));

    private static IQueryable<Order> Detailed(AppDbContext db) => db.Orders
        .Include(o => o.Items).ThenInclude(i => i.Dish)
        .Include(o => o.Shortages).ThenInclude(s => s.Product);

    private static OrderResponse ToResponse(Order o) => ToResponse(o, []);

    private static OrderResponse ToResponse(Order o, IEnumerable<OrderWriteOff> writeOffs) => new(
        o.Id, o.Number, o.CreatedAt, o.Status, o.Place, Total(o),
        o.Items.Select(i => new OrderItemResponse(i.DishId, i.Dish!.Name, i.Quantity, i.DiscountPercent, i.DiscountQuantity, i.Price)),
        o.Shortages.Select(s => new ShortageResponse(s.ProductId, s.Product!.Name, s.Product.Unit, s.Required, s.Available)),
        writeOffs, o.ClosedAt, o.ReadyAt, o.Source, o.Delivery);

    /// <summary>Сумма заказа; цена со скидкой округляется до рубля, как её показывает горячее предложение.</summary>
    public static decimal Total(Order o) => o.Items.Sum(LineTotal);

    public static decimal LineTotal(OrderItem i) =>
        i.Price * (i.Quantity - i.DiscountQuantity) + Math.Round(i.Price * (100 - i.DiscountPercent) / 100) * i.DiscountQuantity;
}
