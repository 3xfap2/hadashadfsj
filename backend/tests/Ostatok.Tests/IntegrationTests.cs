using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;
using Ostatok.Api.Features;

namespace Ostatok.Tests;

/// <summary>Внешние системы по ключам с правами: поставщик, касса, сайт кафе; печать журнала движений.</summary>
public class IntegrationTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiFixture.Json))!;
    }

    private static async Task<T> Get<T>(HttpClient client, string url) => (await client.GetFromJsonAsync<T>(url, ApiFixture.Json))!;

    /// <summary>Ресторан с продуктом на складе и блюдом из него: на порцию идёт 0,2 кг, на складе 1 кг — пять порций.</summary>
    private async Task<(HttpClient Manager, Guid Restaurant, ProductResponse Product, DishResponse Dish)> RestaurantAsync()
    {
        var (manager, _, _) = await fx.NewRestaurantAsync($"Кафе {Guid.NewGuid():N}"[..20]);
        var restaurant = (await Get<MeResponse>(manager, "/api/me")).RestaurantId;
        var product = await Read<ProductResponse>(await manager.PostAsJsonAsync("/api/products", new ProductRequest("Томаты", Unit.Kg, 72, 200), ApiFixture.Json));
        var dish = await Read<DishResponse>(await manager.PostAsJsonAsync("/api/dishes", new DishRequest("Салат", 450, [new RecipeItemDto(product.Id, 0.2m)])));
        (await manager.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, "П-1", null, [new SupplyItemRequest(product.Id, 1, null)]))).EnsureSuccessStatusCode();
        return (manager, restaurant, product, dish);
    }

    private static async Task<HttpClient> KeyAsync(ApiFixture fx, HttpClient manager, string name, params string[] rights)
    {
        var issued = await Read<ApiKeyIssued>(await manager.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest(name, [.. rights])));
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", issued.Key);
        return client;
    }

    private static Task<HttpResponseMessage> External(HttpClient key, string id, Guid dish, int quantity, bool delivery = true) =>
        key.PostAsJsonAsync("/api/orders/external", new ExternalOrderRequest(id, [new OrderLine(dish, quantity)], Delivery: delivery));

    private static async Task<decimal> Fresh(HttpClient manager, ProductResponse product) =>
        (await Get<List<StockRow>>(manager, "/api/stock")).Single(r => r.ProductId == product.Id).Fresh;

    [Fact]
    public async Task У_каждой_системы_свой_ключ_и_он_открывает_только_свои_права()
    {
        var (manager, _, product, dish) = await RestaurantAsync();
        using var supplier = await KeyAsync(fx, manager, "Поставщик ФрешПоставка", "supplies");
        using var pos = await KeyAsync(fx, manager, "Касса зала", "orders");

        // Справочник продуктов — по ключу с любым правом, без цен.
        var products = await supplier.GetAsync("/api/integration/products");
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
        Assert.Equal("Томаты", Assert.Single((await Read<List<IntegrationProduct>>(products))).Name);
        Assert.Equal(HttpStatusCode.OK, (await pos.GetAsync("/api/integration/products")).StatusCode);

        // Ключом поставщика нельзя оформить заказ и читать меню, ключом кассы — прислать поставку.
        var menu = await supplier.GetAsync("/api/integration/menu");
        Assert.Equal(HttpStatusCode.Forbidden, menu.StatusCode);
        Assert.Contains("«Поставщик ФрешПоставка» не даёт права оформлять заказы", await menu.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await External(supplier, "S-1", dish.Id, 1)).StatusCode);
        var supply = await pos.PostAsJsonAsync("/api/supplies/incoming",
            new SupplyRequest(Guid.NewGuid(), "ВН-1", null, [new SupplyItemRequest(Guid.Empty, 1, 48, ProductName: "Томаты")], "ООО «Овощебаза»", "7701234567"));
        Assert.Equal(HttpStatusCode.Forbidden, supply.StatusCode);
        Assert.Contains("присылать поставки", await supply.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Accepted, (await supplier.PostAsJsonAsync("/api/supplies/incoming",
            new SupplyRequest(Guid.NewGuid(), "ВН-2", null, [new SupplyItemRequest(Guid.Empty, 1, 48, ProductName: "томаты")], "ООО «Овощебаза»", "7701234567"))).StatusCode);

        // Управляющий видит ключи с правами и временем использования; сам ключ в списке не показывается.
        var keys = await Get<List<ApiKeyRow>>(manager, "/api/restaurant/keys");
        var posRow = keys.Single(k => k.Name == "Касса зала");
        Assert.Equal(new[] { "orders" }, posRow.Rights);
        Assert.NotNull(posRow.LastUsedAt);
        Assert.True(posRow.CanRevoke);
        Assert.True((await Get<RestaurantSettings>(manager, "/api/restaurant")).HasIntegrationKey);

        // Отозванный ключ перестаёт работать, остальные — нет.
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsync($"/api/restaurant/keys/{posRow.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pos.GetAsync("/api/integration/menu")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supplier.GetAsync("/api/integration/products")).StatusCode);
        Assert.NotNull((await Get<List<ApiKeyRow>>(manager, "/api/restaurant/keys")).Single(k => k.Id == posRow.Id).RevokedAt);

        // Ключ без прав и без названия не выдаётся; ключами управляет только управляющий.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest("Без прав", []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest(" ", ["orders"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest("Лишнее", ["admin"]))).StatusCode);
        using var storekeeper = await fx.SignInAsync("storekeeper");
        Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.GetAsync("/api/restaurant/keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest("Своя касса", ["orders"]))).StatusCode);
    }

    [Fact]
    public async Task Заказ_извне_проходит_тем_же_путём_что_и_у_официанта()
    {
        var (manager, _, product, dish) = await RestaurantAsync();
        using var site = await KeyAsync(fx, manager, "Сайт кафе", "orders");

        var menu = await Read<IntegrationMenu>(await site.GetAsync("/api/integration/menu"));
        var row = Assert.Single(menu.Dishes);
        Assert.Equal((dish.Id, 450m, true, (int?)5), (row.DishId, row.Price, row.Available, row.Portions));

        var placed = await External(site, "SITE-1001", dish.Id, 2);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var order = await Read<ExternalOrderResponse>(placed);
        Assert.Equal((OrderStatus.Accepted, "SITE-1001", "Сайт кафе", true, 900m), (order.Status, order.ExternalId, order.Source, order.Delivery, order.Total));
        Assert.Equal(0.6m, await Fresh(manager, product));

        // Повтор того же заказа (обрыв связи, двойная отправка) возвращает тот же заказ и ничего не списывает.
        var repeated = await Read<ExternalOrderResponse>(await External(site, "SITE-1001", dish.Id, 2));
        Assert.Equal((order.Id, order.Number), (repeated.Id, repeated.Number));
        Assert.Equal(0.6m, await Fresh(manager, product));

        // Исполнитель в журнале — название ключа; на кухне заказ виден с пометкой источника.
        var movements = await Get<List<MovementRow>>(manager, "/api/movements");
        Assert.Equal("Сайт кафе", movements.Single(m => m.OrderId == order.Id).Actor);
        var kitchen = await Get<KitchenBoard>(manager, "/api/orders/kitchen");
        Assert.Equal(("Сайт кафе", true), kitchen.Queue.Single(o => o.Id == order.Id) is var k ? (k.Source, k.Delivery) : default);

        // Нехватка: 409 с перечнем блюд, со склада ничего не списано.
        var refused = await External(site, "SITE-1002", dish.Id, 4);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var rejected = (await refused.Content.ReadFromJsonAsync<ExternalOrderResponse>(ApiFixture.Json))!;
        Assert.Equal((OrderStatus.Rejected, "Салат"), (rejected.Status, Assert.Single(rejected.Unavailable)));
        Assert.Contains("Ничего не списано", rejected.Detail);
        Assert.Equal(0.6m, await Fresh(manager, product));

        // Блюдо снято с продажи: 409 с перечнем, заказ не создаётся.
        (await manager.PostAsync($"/api/dishes/{dish.Id}/stop", null)).EnsureSuccessStatusCode();
        Assert.False(Assert.Single((await Read<IntegrationMenu>(await site.GetAsync("/api/integration/menu"))).Dishes).Available);
        var stopped = await External(site, "SITE-1003", dish.Id, 1);
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains("Снято с продажи: Салат", await stopped.Content.ReadAsStringAsync());
        Assert.Equal(0.6m, await Fresh(manager, product));

        // Ошибки в данных: нет номера, место у доставки, несуществующий стол.
        Assert.Equal(HttpStatusCode.BadRequest, (await External(site, "", dish.Id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/api/orders/external", new ExternalOrderRequest("X-1", [new OrderLine(dish.Id, 1)], "Стол 1", Delivery: true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/api/orders/external", new ExternalOrderRequest("X-2", [new OrderLine(dish.Id, 1)], "Стол 999"))).StatusCode);
    }

    [Fact]
    public async Task Ключ_другого_ресторана_не_видит_чужое_меню_и_не_заказывает_чужие_блюда()
    {
        var (_, _, _, dish) = await RestaurantAsync();
        var (other, _, _, _) = await RestaurantAsync();
        using var key = await KeyAsync(fx, other, "Касса другого кафе", "orders");

        Assert.DoesNotContain((await Read<IntegrationMenu>(await key.GetAsync("/api/integration/menu"))).Dishes, d => d.DishId == dish.Id);
        var order = await External(key, "OTHER-1", dish.Id, 1);
        Assert.Equal(HttpStatusCode.BadRequest, order.StatusCode);
        Assert.Contains("неизвестное блюдо", await order.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Без_ключа_401_а_частые_запросы_с_ключа_отсекаются()
    {
        var (manager, _, _, _) = await RestaurantAsync();
        using var anonymous = fx.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/integration/products")).StatusCode);
        // Вход сотрудника ключ не заменяет.
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.GetAsync("/api/integration/menu")).StatusCode);

        using var key = await KeyAsync(fx, manager, "Шумная касса", "orders");
        for (var i = 0; i < IntegrationKey.PerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/integration/products")).StatusCode);
        var limited = await key.GetAsync("/api/integration/products");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // Через минуту ключ снова работает.
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/integration/products")).StatusCode);
    }

    [Fact]
    public async Task В_демо_ресторане_посетитель_не_отзывает_ключ_для_показа()
    {
        using var manager = await fx.SignInAsync("manager");
        var fresh = await Read<ApiKeyIssued>(await manager.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest("Ключ посетителя", ["orders"])));
        // Свой только что выданный ключ посетитель убирает сам.
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsync($"/api/restaurant/keys/{fresh.Id}/revoke", null)).StatusCode);

        var show = await Read<ApiKeyIssued>(await manager.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest("Ключ для показа", ["supplies", "orders"])));
        fx.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.False((await Get<List<ApiKeyRow>>(manager, "/api/restaurant/keys")).Single(k => k.Id == show.Id).CanRevoke);
        var refused = await manager.PostAsync($"/api/restaurant/keys/{show.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("демо-ресторан", await refused.Content.ReadAsStringAsync());
        using var key = fx.CreateClient();
        key.DefaultRequestHeaders.Add("X-Api-Key", show.Key);
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/integration/menu")).StatusCode);

        // В своём ресторане управляющий отзывает любой ключ.
        var (own, _, _, _) = await RestaurantAsync();
        var old = await Read<ApiKeyIssued>(await own.PostAsJsonAsync("/api/restaurant/keys", new ApiKeyRequest("Старая касса", ["orders"])));
        fx.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.NoContent, (await own.PostAsync($"/api/restaurant/keys/{old.Id}/revoke", null)).StatusCode);
    }

    private async Task<int> SealAsync(Guid restaurant)
    {
        await using var scope = fx.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().Id = restaurant;
        return await JournalSeal.SealAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), fx.Time.GetUtcNow(), CancellationToken.None);
    }

    [Fact]
    public async Task Печать_журнала_замечает_правку_движения_в_базе()
    {
        var (manager, restaurant, product, dish) = await RestaurantAsync();
        using var site = await KeyAsync(fx, manager, "Сайт", "orders");
        (await External(site, "J-1", dish.Id, 1)).EnsureSuccessStatusCode();
        (await External(site, "J-2", dish.Id, 1)).EnsureSuccessStatusCode();

        var before = await Get<JournalVerifyResult>(manager, "/api/stock/journal/verify");
        Assert.Equal((true, 0, 3), (before.Intact, before.Sealed, before.Unsealed));

        // Цепочка строится: три движения (поставка и два заказа) запечатаны по порядку.
        Assert.Equal(3, await SealAsync(restaurant));
        Assert.Equal(0, await SealAsync(restaurant));
        var sealedOk = await Get<JournalVerifyResult>(manager, "/api/stock/journal/verify");
        Assert.Equal((true, 3, 0), (sealedOk.Intact, sealedOk.Sealed, sealedOk.Unsealed));
        Assert.Equal(64, sealedOk.LastHash!.Length);

        // Новые движения после печати проверку не ломают: они ждут следующей печати и встают в цепочку следом.
        (await External(site, "J-3", dish.Id, 1)).EnsureSuccessStatusCode();
        var waiting = await Get<JournalVerifyResult>(manager, "/api/stock/journal/verify");
        Assert.Equal((true, 3, 1), (waiting.Intact, waiting.Sealed, waiting.Unsealed));
        Assert.Equal(1, await SealAsync(restaurant));
        Assert.Equal((true, 4), await Get<JournalVerifyResult>(manager, "/api/stock/journal/verify") is var v ? (v.Intact, v.Sealed) : default);

        // Кто-то с доступом к базе «уменьшил» списание по второму заказу — проверка называет эту запись.
        var movements = (await Get<List<MovementRow>>(manager, "/api/movements")).OrderBy(m => m.Id).ToList();
        var forged = movements[2];
        await using (var scope = fx.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Movements" SET "Quantity" = 0.1 WHERE "Id" = {forged.Id}""");
        }
        var broken = await Get<JournalVerifyResult>(manager, "/api/stock/journal/verify");
        Assert.False(broken.Intact);
        Assert.Equal((forged.Id, 3L), (broken.BrokenMovementId, broken.BrokenSealNo));
        Assert.Contains("№ 3", broken.Message);

        // Проверка — для управляющего.
        using var storekeeper = await fx.SignInAsync("storekeeper");
        Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.GetAsync("/api/stock/journal/verify")).StatusCode);
    }

    [Fact]
    public async Task Удалённая_из_журнала_запись_видна_по_пропуску_номера()
    {
        var (manager, restaurant, _, dish) = await RestaurantAsync();
        using var site = await KeyAsync(fx, manager, "Сайт", "orders");
        (await External(site, "D-1", dish.Id, 1)).EnsureSuccessStatusCode();
        (await External(site, "D-2", dish.Id, 1)).EnsureSuccessStatusCode();
        await SealAsync(restaurant);

        var middle = (await Get<List<MovementRow>>(manager, "/api/movements")).OrderBy(m => m.Id).ElementAt(1);
        await using (var scope = fx.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Movements" WHERE "Id" = {middle.Id}""");
        }
        var broken = await Get<JournalVerifyResult>(manager, "/api/stock/journal/verify");
        Assert.False(broken.Intact);
        Assert.Equal(3L, broken.BrokenSealNo);
        Assert.Contains("удалена", broken.Message);
    }
}
