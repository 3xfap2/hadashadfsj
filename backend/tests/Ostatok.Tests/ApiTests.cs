using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Ostatok.Api.Domain;
using Ostatok.Api.Features;

namespace Ostatok.Tests;

/// <summary>Сквозные сценарии ТЗ через HTTP. Каждый тест заводит свои продукты и блюда, общих данных нет.</summary>
public class ApiTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private HttpClient Api => fx.Api;

    private async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ApiFixture.Json))!;

    private async Task<T> Get<T>(string url) => (await Api.GetFromJsonAsync<T>(url, ApiFixture.Json))!;

    private async Task<ProductResponse> Product(int shelfLifeHours = 48)
    {
        var response = await Api.PostAsJsonAsync("/api/products",
            new ProductRequest($"Продукт {Guid.NewGuid():N}", Unit.Kg, shelfLifeHours, 1000), ApiFixture.Json);
        response.EnsureSuccessStatusCode();
        return await Read<ProductResponse>(response);
    }

    private async Task<DishResponse> Dish(params (ProductResponse Product, decimal Quantity)[] recipe)
    {
        var response = await Api.PostAsJsonAsync("/api/dishes",
            new DishRequest($"Блюдо {Guid.NewGuid():N}", 500, recipe.Select(r => new RecipeItemDto(r.Product.Id, r.Quantity)).ToList()));
        response.EnsureSuccessStatusCode();
        return await Read<DishResponse>(response);
    }

    private async Task<SupplyResponse> Supply(params SupplyItemRequest[] items)
    {
        var response = await Api.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, items.ToList()));
        response.EnsureSuccessStatusCode();
        return await Read<SupplyResponse>(response);
    }

    private Task<HttpResponseMessage> Order(DishResponse dish, int quantity = 1) =>
        fx.Waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, quantity)]));

    private async Task<StopListRow> StopListRow(DishResponse dish) =>
        (await Get<List<StopListRow>>("/api/stop-list")).Single(r => r.DishId == dish.Id);

    private Task<List<BatchRow>> Batches(ProductResponse product) =>
        Get<List<BatchRow>>($"/api/stock/batches?productId={product.Id}");

    [Fact]
    public async Task Без_токена_доступа_нет()
    {
        using var anonymous = fx.CreateClient();

        var response = await anonymous.GetAsync("/api/stock");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Поставка_создаёт_партии_со_сроком_от_даты_поставки()
    {
        var product = await Product(shelfLifeHours: 48);

        var supply = await Supply(new SupplyItemRequest(product.Id, 2, null), new SupplyItemRequest(product.Id, 3, 10));

        var now = fx.Time.GetUtcNow();
        Assert.Equal([now.AddHours(48), now.AddHours(10)], supply.Items.Select(i => i.ExpiresAt));
        var stock = (await Get<List<StockRow>>("/api/stock")).Single(r => r.ProductId == product.Id);
        Assert.Equal((5m, 2, now.AddHours(10)), (stock.Fresh, stock.Batches, stock.NearestExpiry!.Value));
    }

    [Fact]
    public async Task Повторная_поставка_с_тем_же_номером_отклоняется()
    {
        var product = await Product();
        var request = new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null)]);

        await Api.PostAsJsonAsync("/api/supplies", request);
        var second = await Api.PostAsJsonAsync("/api/supplies", request);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(await Batches(product));
    }

    [Fact]
    public async Task Заказ_списывает_сначала_партию_с_ранним_сроком()
    {
        var product = await Product();
        var dish = await Dish((product, 0.4m));
        await Supply(new SupplyItemRequest(product.Id, 1, 40));
        await Supply(new SupplyItemRequest(product.Id, 0.5m, 5));

        var response = await Order(dish, quantity: 2);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Ответ называет, из каких партий списано: сначала ранняя, затем поздняя.
        Assert.Equal([0.5m, 0.3m], (await Read<OrderResponse>(response)).WriteOffs.Select(w => w.Quantity));
        var batches = await Batches(product);
        // Ранняя партия (0,5) ушла целиком, остаток 0,3 добран из поздней.
        Assert.Equal([0.7m], batches.Select(b => b.Remaining));
        var movements = (await Get<List<MovementRow>>("/api/movements?take=500"))
            .Where(m => m.ProductId == product.Id && m.Type == MovementType.Order);
        Assert.Equal([0.3m, 0.5m], movements.Select(m => m.Quantity).Order());
    }

    [Fact]
    public async Task Заказ_с_нехваткой_отклоняется_и_ничего_не_списывает()
    {
        var enough = await Product();
        var scarce = await Product();
        var dish = await Dish((enough, 0.2m), (scarce, 0.3m));
        await Supply(new SupplyItemRequest(enough.Id, 5, null), new SupplyItemRequest(scarce.Id, 0.5m, null));

        var response = await Order(dish, quantity: 2);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var order = await Read<OrderResponse>(response);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        var shortage = Assert.Single(order.Shortages);
        Assert.Equal((scarce.Id, 0.6m, 0.5m), (shortage.ProductId, shortage.Required, shortage.Available));
        Assert.Equal(5m, (await Batches(enough)).Single().Remaining);
    }

    [Fact]
    public async Task Блюдо_уходит_в_стоп_когда_продукт_закончился_и_возвращается_после_поставки()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        Assert.Equal((DishStatus.Stop, StopReason.OutOfStock), ((await StopListRow(dish)).Status, (await StopListRow(dish)).Reason));

        await Supply(new SupplyItemRequest(product.Id, 1, null));
        Assert.Equal((DishStatus.Low, 2), ((await StopListRow(dish)).Status, (await StopListRow(dish)).Portions));

        (await Order(dish, quantity: 2)).EnsureSuccessStatusCode();
        Assert.Equal(DishStatus.Stop, (await StopListRow(dish)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Order(dish)).StatusCode);

        await Supply(new SupplyItemRequest(product.Id, 10, null));
        Assert.Equal((DishStatus.Available, 20), ((await StopListRow(dish)).Status, (await StopListRow(dish)).Portions));
    }

    [Fact]
    public async Task Просроченная_партия_не_продаётся_и_ставит_блюдо_в_стоп()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        await Supply(new SupplyItemRequest(product.Id, 3, 2));

        fx.Time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(HttpStatusCode.Conflict, (await Order(dish)).StatusCode);
        Assert.Equal(StopReason.Expired, (await StopListRow(dish)).Reason);
        var batch = (await Batches(product)).Single();
        Assert.Equal((BatchStatus.Expired, 3m), (batch.Status, batch.Remaining));

        var writeOff = await Api.PostAsync($"/api/stock/batches/{batch.Id}/write-off", null);

        Assert.Equal(HttpStatusCode.NoContent, writeOff.StatusCode);
        Assert.Empty(await Batches(product));
        Assert.Equal(StopReason.OutOfStock, (await StopListRow(dish)).Reason);
    }

    [Fact]
    public async Task Партия_помечается_истекающей_за_сутки_до_срока()
    {
        var product = await Product();
        await Supply(new SupplyItemRequest(product.Id, 1, 10), new SupplyItemRequest(product.Id, 1, 100));

        var batches = await Batches(product);

        Assert.Equal([BatchStatus.ExpiringSoon, BatchStatus.Fresh], batches.Select(b => b.Status));
        Assert.Equal(10, batches[0].HoursLeft);
    }

    [Fact]
    public async Task Параллельные_заказы_не_списывают_больше_чем_есть()
    {
        var product = await Product();
        var dish = await Dish((product, 0.2m));
        await Supply(new SupplyItemRequest(product.Id, 0.6m, 20), new SupplyItemRequest(product.Id, 0.4m, 40));

        // Запаса ровно на 5 порций, заказов 20 — одновременно.
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Order(dish)));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(15, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Empty(await Batches(product));
        var written = (await Get<List<MovementRow>>("/api/movements?take=500"))
            .Where(m => m.ProductId == product.Id && m.Type == MovementType.Order)
            .Sum(m => m.Quantity);
        Assert.Equal(1.0m, written);
    }

    [Fact]
    public async Task Некорректные_данные_отклоняются_с_описанием()
    {
        var product = await Product();

        var emptyOrder = await fx.Waiter.PostAsJsonAsync("/api/orders", new OrderRequest([]));
        var negativeSupply = await Api.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, "П-ошибка", null, [new SupplyItemRequest(product.Id, -1, null)]));
        var dishWithoutRecipe = await Api.PostAsJsonAsync("/api/dishes", new DishRequest("Пустое блюдо", 100, []));

        Assert.All([emptyOrder, negativeSupply, dishWithoutRecipe], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    [Fact]
    public async Task Горячее_предложение_появляется_и_исчезает_вслед_за_остатками()
    {
        var product = await Product();
        var dish = await Dish((product, 0.2m));
        await Supply(new SupplyItemRequest(product.Id, 0.4m, 5));

        var offer = (await Get<List<HotOffer>>("/api/hot-offers")).Single(o => o.DishId == dish.Id);
        Assert.Equal((2, 400m), (offer.Portions, offer.ValueAtRisk));
        Assert.Contains("истекает через 5 ч", offer.Reason);

        (await Order(dish, quantity: 2)).EnsureSuccessStatusCode();

        Assert.DoesNotContain(await Get<List<HotOffer>>("/api/hot-offers"), o => o.DishId == dish.Id);
    }

    [Fact]
    public async Task Стоп_лист_показывает_когда_блюдо_уйдёт_в_стоп()
    {
        var product = await Product();
        var dish = await Dish((product, 0.2m));
        await Supply(new SupplyItemRequest(product.Id, 1, 5));

        Assert.Equal(fx.Time.GetUtcNow().AddHours(5), (await StopListRow(dish)).StopForecastAt);
    }

    [Fact]
    public async Task Просрочка_списывается_автоматически_после_отсрочки_и_попадает_в_аналитику()
    {
        var product = await Product();
        // Срок хранения 20 часов — дольше отсрочки, поэтому партия ждёт списания все 12 часов.
        await Supply(new SupplyItemRequest(product.Id, 2, 20));
        async Task AutoWriteOff()
        {
            await using var scope = fx.DemoScope();
            await scope.ServiceProvider.GetRequiredService<WriteOffService>().WriteOffExpiredAsync(TimeSpan.FromHours(12), default);
        }

        fx.Time.Advance(TimeSpan.FromHours(21));
        await AutoWriteOff();
        Assert.Equal(BatchStatus.Expired, (await Batches(product)).Single().Status);

        fx.Time.Advance(TimeSpan.FromHours(12));
        await AutoWriteOff();

        Assert.Empty(await Batches(product));
        var report = await Get<WriteOffReport>("/api/analytics/write-offs?days=7");
        var row = report.ByProduct.Single(p => p.ProductId == product.Id);
        Assert.Equal((2m, 2000m), (row.Quantity, row.Value));
        Assert.Contains(report.ByReason, r => r.Reason.Contains("автосписание"));
        Assert.Equal(7, report.ByDay.Count());
        Assert.Equal(report.Total, report.ByDay.Sum(d => d.Value));
    }

    [Fact]
    public async Task Закупки_и_дефицит_отражают_нехватку_продукта()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        Assert.Equal(HttpStatusCode.Conflict, (await Order(dish)).StatusCode);

        var suggestion = (await Get<List<PurchaseRow>>("/api/purchase-suggestions")).Single(s => s.ProductId == product.Id);
        var deficit = (await Get<List<DeficitRow>>("/api/analytics/deficit")).Single(r => r.ProductId == product.Id);

        Assert.Equal(5m, suggestion.Quantity);
        Assert.Equal((1, 0.5m), (deficit.RejectedOrders, deficit.MissingQuantity));
    }

    private Task<HttpResponseMessage> Login(string login, string password) =>
        fx.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password));

    private static async Task<int> Extension(HttpResponseMessage response, string name) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty(name).GetInt32();

    [Fact]
    public async Task После_пяти_неверных_паролей_вход_закрывается_на_десять_минут()
    {
        var login = $"user-{Guid.NewGuid():N}";

        for (var left = 4; left >= 1; left--)
        {
            var wrong = await Login(login, "нет");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(left, await Extension(wrong, "attemptsLeft"));
        }
        var fifth = await Login(login, "нет");
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal(10, await Extension(fifth, "retryAfterMinutes"));

        fx.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(4, await Extension(await Login(login, "нет"), "attemptsLeft"));
    }

    [Fact]
    public async Task Успешный_вход_сбрасывает_счёт_неверных_попыток()
    {
        Assert.Equal(4, await Extension(await Login("demo", "нет"), "attemptsLeft"));
        Assert.Equal(3, await Extension(await Login("demo", "нет"), "attemptsLeft"));

        (await Login("demo", "demo2026")).EnsureSuccessStatusCode();

        Assert.Equal(4, await Extension(await Login("demo", "нет"), "attemptsLeft"));
        (await Login("demo", "demo2026")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Кабинет_знает_должность_и_часы_смены()
    {
        var me = await Get<MeResponse>("/api/me");

        Assert.Equal(("demo", "Шеф-повар", "12:00", "24:00"), (me.Login, me.Title, me.Shift.Start, me.Shift.End));
        Assert.Equal(Role.Chef, me.Role);
    }

    [Fact]
    public async Task Заказ_запоминает_место_и_не_принимает_неизвестное()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        await Supply(new SupplyItemRequest(product.Id, 1, null));

        var atTable = await fx.Waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)], "Стол 7"));
        var unknown = await fx.Waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)], "Крыша"));

        Assert.Equal("Стол 7", (await Read<OrderResponse>(atTable)).Place);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Стол 7", (await Get<MeResponse>("/api/me")).Places);
    }

    [Fact]
    public async Task Вопрос_с_лендинга_доходит_до_управляющего_и_ответ_возвращается_посетителю()
    {
        using var visitor = fx.CreateClient();
        var name = $"Гость {Guid.NewGuid():N}";

        var noContact = await visitor.PostAsJsonAsync("/api/chats", new ChatStartRequest(name, "", "Сколько стоит?"));
        var started = await visitor.PostAsJsonAsync("/api/chats", new ChatStartRequest(name, "+7 900 000-00-00", "Сколько стоит?"));
        var chat = (await started.Content.ReadFromJsonAsync<ChatResponse>(ApiFixture.Json))!;
        await visitor.PostAsJsonAsync($"/api/chats/{chat.Id}/messages", new ChatTextRequest("И есть ли приложение?"));

        Assert.Equal(HttpStatusCode.BadRequest, noContact.StatusCode);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        // Список обращений закрыт и для посетителя, и для шеф-повара (demo).
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/support/chats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api.GetAsync("/api/support/chats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/chats/{Guid.NewGuid()}")).StatusCode);

        using var manager = await fx.SignInAsync("manager");
        var row = Assert.Single((await manager.GetFromJsonAsync<List<ChatRow>>("/api/support/chats", ApiFixture.Json))!, c => c.Name == name);
        Assert.Equal(2, row.Unread);
        Assert.Equal("И есть ли приложение?", row.LastText);

        var answer = await manager.PostAsJsonAsync($"/api/support/chats/{chat.Id}/messages", new ChatTextRequest("Да, для Android."));
        Assert.Equal(HttpStatusCode.Created, answer.StatusCode);

        var forVisitor = (await visitor.GetFromJsonAsync<ChatResponse>($"/api/chats/{chat.Id}", ApiFixture.Json))!;
        Assert.Equal(["Сколько стоит?", "И есть ли приложение?", "Да, для Android."], forVisitor.Messages.Select(m => m.Text));
        Assert.True(forVisitor.Messages[^1].FromStaff);
        row = Assert.Single((await manager.GetFromJsonAsync<List<ChatRow>>("/api/support/chats", ApiFixture.Json))!, c => c.Name == name);
        Assert.Equal(0, row.Unread);
    }

    [Fact]
    public async Task Заготовка_списывает_сырьё_и_приходит_на_склад_партией_со_своим_сроком()
    {
        var cream = await Product(shelfLifeHours: 120);
        var cheese = await Product(shelfLifeHours: 720);
        var created = await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(cream.Id, 0.5m), new RecipeItemDto(cheese.Id, 0.2m)]), ApiFixture.Json);
        var sauce = await Read<ProductResponse>(created);
        var dish = await Dish((sauce, 0.1m));
        // Сливки двумя партиями: в заготовку должна уйти та, что истекает раньше.
        await Supply(new SupplyItemRequest(cream.Id, 1, 100), new SupplyItemRequest(cream.Id, 1, 10), new SupplyItemRequest(cheese.Id, 1, null));

        // Цена заготовки считается по составу: 0,5 × 1000 + 0,2 × 1000.
        Assert.Equal(700, sauce.Cost);
        Assert.Equal(DishStatus.Stop, (await StopListRow(dish)).Status);
        var plan = (await Get<List<PrepRow>>("/api/preps")).Single(p => p.ProductId == sauce.Id);
        Assert.Equal(4, plan.MaxQuantity);

        var tooMuch = await Api.PostAsJsonAsync("/api/preps", new PrepRequest(sauce.Id, 6));
        var made = await Api.PostAsJsonAsync("/api/preps", new PrepRequest(sauce.Id, 2));

        // На 6 л не хватает сыра (нужно 1,2 кг, есть 1) и сливок: ничего не списано.
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Contains((await Read<PrepResponse>(tooMuch)).Shortages, s => s.ProductId == cheese.Id && s.Required == 1.2m && s.Available == 1);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var response = await Read<PrepResponse>(made);
        Assert.Equal(fx.Time.GetUtcNow().AddHours(48), response.ExpiresAt);
        Assert.StartsWith("З-", response.SupplyNumber);
        // Партия сливок с ранним сроком израсходована полностью, поздняя не тронута.
        Assert.Equal([1m], (await Batches(cream)).Select(b => b.Remaining));
        Assert.Equal(0.6m, (await Batches(cheese)).Single().Remaining);
        Assert.Equal(2, (await Batches(sauce)).Single().Remaining);
        Assert.Equal(DishStatus.Available, (await StopListRow(dish)).Status);

        // Заготовку нельзя включить в состав другой заготовки, а закупать её не предлагают.
        var nested = await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(sauce.Id, 0.5m)]), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);
        Assert.DoesNotContain(await Get<List<PurchaseRow>>("/api/purchase-suggestions"), p => p.ProductId == sauce.Id);
    }

    [Fact]
    public async Task Скидку_горячего_предложения_назначает_сервер_а_стол_закрывается()
    {
        var product = await Product(shelfLifeHours: 48);
        var dish = await Dish((product, 0.2m));
        var longLife = await Product(shelfLifeHours: 720);
        var calm = await Dish((longLife, 0.2m));
        // 2 кг истекают через 4 часа, продаж не было: горячее предложение со скидкой 35% (цена 500 → 325).
        await Supply(new SupplyItemRequest(product.Id, 2, 4), new SupplyItemRequest(longLife.Id, 5, null));

        var discounted = await Read<OrderResponse>(await fx.Waiter.PostAsJsonAsync("/api/orders",
            new OrderRequest([new OrderLine(dish.Id, 2, Discount: true), new OrderLine(calm.Id, 1, Discount: true)], "Стол 7")));

        // У второго блюда ничего не истекает: скидку просили, но сервер её не дал.
        Assert.Equal([35, 0], discounted.Items.Select(i => i.DiscountPercent).OrderDescending());
        Assert.Equal(325 * 2 + 500, discounted.Total);

        var closed = await fx.Waiter.PostAsJsonAsync("/api/orders/close", new CloseTableRequest("Стол 7"));
        var again = await fx.Waiter.PostAsJsonAsync("/api/orders/close", new CloseTableRequest("Стол 7"));

        Assert.True((await Read<CloseTableResponse>(closed)).Total >= 1150);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.NotNull((await Get<List<OrderResponse>>("/api/orders?take=200")).Single(o => o.Id == discounted.Id).ClosedAt);
    }

    [Fact]
    public async Task Проверка_сроков_партия_на_минуты_проходит_путь_до_автосписания()
    {
        var product = await Product(shelfLifeHours: 48);
        var dish = await Dish((product, 0.2m));

        var created = await Api.PostAsJsonAsync("/api/simulator/expiry-check", new ExpiryCheckRequest(product.Id, 2));
        var check = await Read<ExpiryCheckResponse>(created);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal((1m, fx.Time.GetUtcNow().AddMinutes(2)), (check.Quantity, check.ExpiresAt));
        Assert.NotEqual(DishStatus.Stop, (await StopListRow(dish)).Status);
        // Блюдо с партией на две минуты — первое в горячих предложениях.
        Assert.Contains(await Get<List<HotOffer>>("/api/hot-offers"), o => o.DishId == dish.Id);

        fx.Time.Advance(TimeSpan.FromMinutes(3));
        // Срок вышел: блюдо в стопе, заказ отклоняется, но отсрочка (2 минуты — срок хранения партии) ещё идёт.
        Assert.Equal((DishStatus.Stop, StopReason.Expired), ((await StopListRow(dish)).Status, (await StopListRow(dish)).Reason));
        Assert.Equal(HttpStatusCode.Conflict, (await Order(dish)).StatusCode);
        using (var scope = fx.DemoScope())
            await scope.ServiceProvider.GetRequiredService<WriteOffService>().WriteOffExpiredAsync(TimeSpan.FromHours(12), default);
        Assert.Null((await Get<ExpiryCheckResponse>("/api/simulator/expiry-check")).WrittenOffAt);

        fx.Time.Advance(TimeSpan.FromMinutes(2));
        using (var scope = fx.DemoScope())
            await scope.ServiceProvider.GetRequiredService<WriteOffService>().WriteOffExpiredAsync(TimeSpan.FromHours(12), default);
        var after = await Get<ExpiryCheckResponse>("/api/simulator/expiry-check");
        Assert.Equal((0m, fx.Time.GetUtcNow()), (after.Remaining, after.WrittenOffAt!.Value));
        Assert.Equal(StopReason.OutOfStock, (await StopListRow(dish)).Reason);
    }

    [Fact]
    public async Task Настройки_горячих_предложений_меняют_список_и_доступны_не_всем()
    {
        var product = await Product(shelfLifeHours: 48);
        var dish = await Dish((product, 0.2m));
        await Supply(new SupplyItemRequest(product.Id, 2, 30));
        Task<List<HotOffer>> Offers() => Get<List<HotOffer>>("/api/hot-offers");

        try
        {
            // Партия истекает через 30 часов: при горизонте 24 часа блюда в списке нет, при 48 — есть.
            await Api.PutAsJsonAsync("/api/hot-offers/settings", new HotRules(HorizonHours: 24));
            Assert.DoesNotContain(await Offers(), o => o.DishId == dish.Id);
            await Api.PutAsJsonAsync("/api/hot-offers/settings", new HotRules());
            Assert.Contains(await Offers(), o => o.DishId == dish.Id);

            await Api.PutAsJsonAsync($"/api/hot-offers/dishes/{dish.Id}", new HotDishRequest(Pinned: true, Hidden: false));
            Assert.Equal(dish.Id, (await Offers())[0].DishId);
            await Api.PutAsJsonAsync($"/api/hot-offers/dishes/{dish.Id}", new HotDishRequest(Pinned: false, Hidden: true));
            Assert.DoesNotContain(await Offers(), o => o.DishId == dish.Id);

            Assert.Equal(HttpStatusCode.BadRequest, (await Api.PutAsJsonAsync("/api/hot-offers/settings", new HotRules(HorizonHours: 1))).StatusCode);
            using var cook = await fx.SignInAsync("cook");
            Assert.Equal(HttpStatusCode.Forbidden, (await cook.PutAsJsonAsync("/api/hot-offers/settings", new HotRules())).StatusCode);
        }
        finally
        {
            await Api.PutAsJsonAsync("/api/hot-offers/settings", new HotRules());
        }
    }

    [Fact]
    public async Task Заявка_с_лендинга_принимается_без_входа_и_видна_в_кабинете()
    {
        using var visitor = fx.CreateClient();
        var organization = $"Ресторан {Guid.NewGuid():N}";

        var accepted = await visitor.PostAsJsonAsync("/api/leads", new LeadRequest("Иван", organization, "ivan@example.com", "2–5", null));
        var badContact = await visitor.PostAsJsonAsync("/api/leads", new LeadRequest("Иван", organization, "ivan@", "1", null));
        var listForVisitor = await visitor.GetAsync("/api/leads");

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badContact.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, listForVisitor.StatusCode);
        // Заявки видит только управляющий; шеф-повару (demo) раздел закрыт.
        using var manager = await fx.SignInAsync("manager");
        Assert.Single((await manager.GetFromJsonAsync<List<LeadResponse>>("/api/leads", ApiFixture.Json))!, l => l.Organization == organization);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api.GetAsync("/api/leads")).StatusCode);
    }

    [Fact]
    public async Task Журнал_записывает_кто_провёл_операцию()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        var supply = await Supply(new SupplyItemRequest(product.Id, 2, 1));
        (await Order(dish)).EnsureSuccessStatusCode();
        fx.Time.Advance(TimeSpan.FromHours(2));
        (await Api.PostAsync($"/api/stock/batches/{supply.Items.Single().BatchId}/write-off", null)).EnsureSuccessStatusCode();

        var movements = (await Get<List<MovementRow>>("/api/movements?take=500")).Where(m => m.ProductId == product.Id).ToList();

        // Поставку и списание провёл шеф-повар, заказ — официант.
        Assert.Equal(["demo", "waiter", "demo"], movements.OrderBy(m => m.Id).Select(m => m.Actor));
    }

    [Fact]
    public async Task Файл_поставки_разбирается_в_предпросмотр_и_ничего_не_сохраняет()
    {
        var product = await Product();
        var csv = $"продукт;количество;единица;срок хранения;цена\n{product.Name};3;кг;72 ч;\nНеизвестный продукт;1;кг;72 ч;";
        // Русский Excel сохраняет CSV в Windows-1251 — такой файл тоже должен читаться.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var content = new ByteArrayContent(System.Text.Encoding.GetEncoding(1251).GetBytes(csv));
        content.Headers.ContentType = new("text/csv");

        var response = await Api.PostAsync("/api/supplies/import/preview", content);

        var rows = await Read<List<ImportRow>>(response);
        Assert.Equal([ImportRowStatus.Ok, ImportRowStatus.Error], rows.Select(r => r.Status));
        Assert.Equal((product.Id, 3m, 72), (rows[0].ProductId!.Value, rows[0].Quantity!.Value, rows[0].ShelfLifeHours!.Value));
        Assert.Empty(await Batches(product));
    }

    [Fact]
    public async Task Заказ_оформляет_только_официант_и_видит_только_заказы_и_меню()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        await Supply(new SupplyItemRequest(product.Id, 1, null));
        var request = new OrderRequest([new OrderLine(dish.Id, 1)]);
        using var manager = await fx.SignInAsync("manager");
        using var cook = await fx.SignInAsync("cook");

        // Управляющий, шеф-повар и повар заказ не оформляют: у заказов своя точка входа.
        Assert.All([await manager.PostAsJsonAsync("/api/orders", request), await Api.PostAsJsonAsync("/api/orders", request), await cook.PostAsJsonAsync("/api/orders", request)],
            r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(HttpStatusCode.Created, (await fx.Waiter.PostAsJsonAsync("/api/orders", request)).StatusCode);

        // Официанту закрыты поставки, аналитика и справочники; меню со стоп-листом и горячие предложения он видит.
        var supply = await fx.Waiter.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, "П-официант", null, [new SupplyItemRequest(product.Id, 1, null)]));
        var analytics = await fx.Waiter.GetAsync("/api/analytics/write-offs");
        var newProduct = await fx.Waiter.PostAsJsonAsync("/api/products", new ProductRequest("Продукт официанта", Unit.Kg, 24, 1), ApiFixture.Json);
        Assert.All([supply, analytics, newProduct], r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.All([await fx.Waiter.GetAsync("/api/stop-list"), await fx.Waiter.GetAsync("/api/hot-offers")], r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        Assert.Equal(["orders", "menu"], (await fx.Waiter.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!.Sections);
        Assert.Equal(["today", "menu", "kitchen"], (await cook.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!.Sections);
    }

    [Fact]
    public async Task Новый_ресторан_работает_в_своём_пространстве_и_не_видит_чужих_данных()
    {
        // В демо-ресторане есть продукт, блюдо и партия.
        var theirs = await Product();
        var theirDish = await Dish((theirs, 0.1m));
        var theirSupply = await Supply(new SupplyItemRequest(theirs.Id, 3, null));

        using var anonymous = fx.CreateClient();
        var login = $"owner{Guid.NewGuid():N}"[..20];
        var registered = await anonymous.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Траттория", login, "trattoria-2026"));
        var taken = await anonymous.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Двойник", login, "trattoria-2026"));
        var weak = await anonymous.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Слабый пароль", $"x{login}"[..20], "123"));
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.BadRequest), (registered.StatusCode, taken.StatusCode, weak.StatusCode));

        using var owner = fx.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new("Bearer", (await Read<LoginResponse>(registered)).Token);
        var me = (await owner.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!;
        Assert.Equal(("Траттория", Role.Manager), (me.Restaurant, me.Role));
        // Обращения с лендинга — раздел команды продукта, у ресторана его нет.
        Assert.DoesNotContain("support", me.Sections);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/support/chats")).StatusCode);

        // Новый ресторан пуст и чужого не видит — ни списком, ни по прямой ссылке.
        Assert.Empty((await owner.GetFromJsonAsync<List<ProductResponse>>("/api/products", ApiFixture.Json))!);
        Assert.Empty((await owner.GetFromJsonAsync<List<StopListRow>>("/api/stop-list", ApiFixture.Json))!);
        Assert.Empty((await owner.GetFromJsonAsync<List<BatchRow>>("/api/stock/batches", ApiFixture.Json))!);
        Assert.Empty((await owner.GetFromJsonAsync<List<MovementRow>>("/api/movements?take=500", ApiFixture.Json))!);
        var foreignWriteOff = await owner.PostAsJsonAsync($"/api/stock/batches/{theirSupply.Items.Single().BatchId}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json);
        var foreignSupply = await owner.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, "П-чужая", null, [new SupplyItemRequest(theirs.Id, 1, null)]));
        Assert.Equal(HttpStatusCode.NotFound, foreignWriteOff.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignSupply.StatusCode);
        Assert.Equal(3, (await Batches(theirs)).Single().Remaining);

        // Свой склад: тот же номер поставки и то же название продукта, что в другом ресторане, не мешают.
        var mine = await Read<ProductResponse>(await owner.PostAsJsonAsync("/api/products", new ProductRequest(theirs.Name, Unit.Kg, 48, 500), ApiFixture.Json));
        var dish = await Read<DishResponse>(await owner.PostAsJsonAsync("/api/dishes", new DishRequest("Паста", 400, [new RecipeItemDto(mine.Id, 0.2m)])));
        var supply = await owner.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, theirSupply.Number, null, [new SupplyItemRequest(mine.Id, 1, null)]));
        Assert.Equal(HttpStatusCode.Created, supply.StatusCode);

        // Управляющий заводит официанта; тот оформляет заказ в своём ресторане, а чужое блюдо заказать не может.
        var waiterLogin = $"w{login}"[..20];
        var staff = await owner.PostAsJsonAsync("/api/users", new StaffRequest(waiterLogin, "waiter-2026", "Официант", Role.Waiter), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Created, staff.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/users", new StaffRequest("demo", "waiter-2026", "Двойник", Role.Cook), ApiFixture.Json)).StatusCode);
        using var waiter = fx.CreateClient();
        var signedIn = await waiter.PostAsJsonAsync("/api/auth/login", new LoginRequest(waiterLogin, "waiter-2026"));
        waiter.DefaultRequestHeaders.Authorization = new("Bearer", (await Read<LoginResponse>(signedIn)).Token);

        var own = await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 2)]));
        var foreign = await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(theirDish.Id, 1)]));
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.BadRequest), (own.StatusCode, foreign.StatusCode));
        Assert.Equal(0.6m, (await owner.GetFromJsonAsync<List<BatchRow>>("/api/stock/batches", ApiFixture.Json))!.Single().Remaining);
        var people = (await owner.GetFromJsonAsync<List<StaffResponse>>("/api/users", ApiFixture.Json))!;
        Assert.Equal(2, people.Count);

        // Управляющий повышает официанта до шеф-повара и выдаёт новый пароль: старый пароль перестаёт подходить,
        // а права меняются со следующего входа.
        var promoted = people.Single(u => u.Login == waiterLogin);
        var update = await owner.PutAsJsonAsync($"/api/users/{promoted.Id}", new StaffUpdate("Шеф-повар", Role.Chef, "chef-2026-new"), ApiFixture.Json);
        Assert.Equal(Role.Chef, (await Read<StaffResponse>(update)).Role);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(waiterLogin, "waiter-2026"))).StatusCode);
        using var chef = fx.CreateClient();
        chef.DefaultRequestHeaders.Authorization = new("Bearer",
            (await Read<LoginResponse>(await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(waiterLogin, "chef-2026-new")))).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await chef.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await chef.GetAsync("/api/analytics/summary")).StatusCode);
        // Себя управляющий понизить не может, чужого сотрудника не видит.
        var self = people.Single(u => u.Login == login);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/users/{self.Id}", new StaffUpdate("Повар", Role.Cook), ApiFixture.Json)).StatusCode);
        using var demoManager = await fx.SignInAsync("manager");
        // Открытые демо-записи защищены: иначе любой посетитель мог бы сменить пароль и закрыть демо остальным.
        var demoCook = (await demoManager.GetFromJsonAsync<List<StaffResponse>>("/api/users", ApiFixture.Json))!.Single(u => u.Login == "cook");
        Assert.Equal(HttpStatusCode.Conflict, (await demoManager.PutAsJsonAsync($"/api/users/{demoCook.Id}", new StaffUpdate("Повар", Role.Cook, "new-password-1"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await demoManager.DeleteAsync($"/api/users/{demoCook.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await demoManager.PutAsJsonAsync($"/api/users/{promoted.Id}", new StaffUpdate("Чужой", Role.Cook), ApiFixture.Json)).StatusCode);

        // Демо-ресторан не видит ни продукта, ни заказа нового ресторана.
        Assert.DoesNotContain(await Get<List<ProductResponse>>("/api/products"), p => p.Id == mine.Id);
        Assert.DoesNotContain(await Get<List<OrderResponse>>("/api/orders?take=200"), o => o.Items.Any(i => i.DishId == dish.Id));
    }

    [Fact]
    public async Task Кладовщик_принимает_поставки_и_списывает_но_не_создаёт_заказы()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        using var storekeeper = await fx.SignInAsync("storekeeper");

        var supply = await storekeeper.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null)]));
        var batchId = (await Read<SupplyResponse>(supply)).Items.Single().BatchId;
        var writeOff = await storekeeper.PostAsJsonAsync($"/api/stock/batches/{batchId}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json);
        var order = await storekeeper.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)]));
        var me = (await storekeeper.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!;

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.NoContent), (supply.StatusCode, writeOff.StatusCode));
        Assert.Equal(HttpStatusCode.Forbidden, order.StatusCode);
        Assert.Equal(["today", "stock", "purchasing"], me.Sections);
        // Управляющему, кроме склада и аналитики, доступны сотрудники и обращения с лендинга.
        using var manager = await fx.SignInAsync("manager");
        Assert.Equal(["today", "stock", "menu", "kitchen", "purchasing", "analytics", "catalog", "staff", "support"],
            (await manager.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!.Sections);
    }

    [Fact]
    public async Task Поставка_хранит_поставщика_и_сумму_а_партия_дату_приёмки()
    {
        var product = await Product();
        var response = await Api.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 2.5m, null)], "Северная рыба"));

        var supply = await Read<SupplyResponse>(response);

        Assert.Equal(("Северная рыба", 2500m), (supply.Supplier, supply.Total));
        Assert.Equal(fx.Time.GetUtcNow(), (await Batches(product)).Single().ReceivedAt);
    }

    [Fact]
    public async Task Файл_Excel_разбирается_так_же_как_CSV()
    {
        var product = await Product();
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var sheet = workbook.AddWorksheet("Поставка");
        string[] header = ["продукт", "количество", "единица", "срок хранения", "цена"];
        for (var c = 0; c < header.Length; c++) sheet.Cell(1, c + 1).Value = header[c];
        sheet.Cell(2, 1).Value = product.Name;
        sheet.Cell(2, 2).Value = 2.5;
        sheet.Cell(2, 3).Value = "кг";
        sheet.Cell(2, 4).Value = "3 дня";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        // Браузер отправляет файл как «application/octet-stream» — сервер определяет формат по содержимому.
        var file = new ByteArrayContent(stream.ToArray());
        file.Headers.ContentType = new("application/octet-stream");
        var response = await Api.PostAsync("/api/supplies/import/preview", file);

        var row = Assert.Single(await Read<List<ImportRow>>(response));
        Assert.Equal((ImportRowStatus.Ok, 2.5m, 72), (row.Status, row.Quantity!.Value, row.ShelfLifeHours!.Value));
    }

    [Fact]
    public async Task Блюдо_снятое_вручную_стоит_в_стопе_и_не_заказывается_пока_его_не_вернут()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        await Supply(new SupplyItemRequest(product.Id, 5, null));

        (await Api.PostAsync($"/api/dishes/{dish.Id}/stop", null)).EnsureSuccessStatusCode();

        var row = await StopListRow(dish);
        Assert.Equal((DishStatus.Stop, StopReason.Manual), (row.Status, row.Reason));
        Assert.Equal(HttpStatusCode.BadRequest, (await Order(dish)).StatusCode);
        Assert.DoesNotContain(await Get<List<HotOffer>>("/api/hot-offers"), o => o.DishId == dish.Id);

        (await Api.PostAsync($"/api/dishes/{dish.Id}/resume", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, (await Order(dish)).StatusCode);
    }

    [Fact]
    public async Task Годная_партия_списывается_только_с_причиной_и_причина_попадает_в_аналитику()
    {
        var product = await Product();
        var supply = await Supply(new SupplyItemRequest(product.Id, 1, null), new SupplyItemRequest(product.Id, 2, null));
        var (first, second) = (supply.Items.First().BatchId, supply.Items.Last().BatchId);

        var noReason = await Api.PostAsync($"/api/stock/batches/{first}/write-off", null);
        var spoiled = await Api.PostAsJsonAsync($"/api/stock/batches/{first}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json);
        var defective = await Api.PostAsJsonAsync($"/api/stock/batches/{second}/write-off", new WriteOffRequest(WriteOffCause.Defective), ApiFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (spoiled.StatusCode, defective.StatusCode));
        var notes = (await Get<List<MovementRow>>("/api/movements?take=500")).Where(m => m.ProductId == product.Id && m.Type == MovementType.WriteOff).Select(m => m.Note);
        Assert.Equal(["Брак поставки", "Порча при хранении"], notes.Order());
    }

    [Fact]
    public async Task Фото_блюда_загружается_и_отдаётся_без_входа()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        byte[] image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        var content = new ByteArrayContent(image);
        content.Headers.ContentType = new("image/jpeg");

        var upload = await Api.PutAsync($"/api/dishes/{dish.Id}/photo", content);
        var text = new ByteArrayContent(image);
        text.Headers.ContentType = new("text/plain");
        var wrongType = await Api.PutAsync($"/api/dishes/{dish.Id}/photo", text);

        upload.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        var saved = (await Get<List<DishResponse>>("/api/dishes")).Single(d => d.Id == dish.Id);
        using var visitor = fx.CreateClient();
        Assert.Equal(image, await visitor.GetByteArrayAsync(saved.PhotoUrl));
    }

    [Fact]
    public async Task Часы_в_стопе_записываются_и_видны_в_аналитике_по_продукту()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        async Task Track()
        {
            await using var scope = fx.DemoScope();
            await scope.ServiceProvider.GetRequiredService<StopTracker>().TrackAsync(default);
        }

        await Track();                                   // блюдо в стопе: продукта нет
        fx.Time.Advance(TimeSpan.FromHours(3));
        await Supply(new SupplyItemRequest(product.Id, 5, null));
        await Track();                                   // привезли — стоп закончился через 3 часа
        fx.Time.Advance(TimeSpan.FromHours(2));
        await Track();

        var row = (await Get<List<DeficitRow>>("/api/analytics/deficit")).Single(r => r.ProductId == product.Id);
        Assert.Equal(3, row.StopHours);
        Assert.Equal([dish.Name], row.Dishes);
        Assert.True((await Get<AnalyticsSummary>("/api/analytics/summary")).StopHours >= 3);
    }

    [Fact]
    public async Task Закупка_называет_поставщика_и_сумму()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        (await Api.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 0.5m, null)], "Мясной двор"))).EnsureSuccessStatusCode();
        (await Order(dish)).EnsureSuccessStatusCode();   // продукт закончился

        var row = (await Get<List<PurchaseRow>>("/api/purchase-suggestions")).Single(r => r.ProductId == product.Id);

        Assert.Equal(("Мясной двор", 5m, 5000m), (row.Supplier, row.Quantity, row.Sum));
    }

    [Fact]
    public async Task Сигнал_о_сроке_поднимается_один_раз_когда_партия_истекает_и_когда_просрочена()
    {
        var product = await Product();
        await Supply(new SupplyItemRequest(product.Id, 2, 30));
        async Task<int> Raise()
        {
            await using var scope = fx.DemoScope();
            return await scope.ServiceProvider.GetRequiredService<AlertService>().RaiseBatchAlertsAsync(default);
        }
        async Task<List<AlertResponse>> Mine() =>
            (await Get<AlertsResponse>("/api/alerts?take=100")).Items.Where(a => a.Title.StartsWith(product.Name)).ToList();

        await Raise();
        Assert.Empty(await Mine());                              // до срока больше суток — сигнала нет

        fx.Time.Advance(TimeSpan.FromHours(10));                 // осталось 20 часов
        await Raise();
        await Raise();                                           // повторный проход не дублирует
        var expiring = Assert.Single(await Mine());
        Assert.Equal((AlertKind.BatchExpiring, false), (expiring.Kind, expiring.Read));

        fx.Time.Advance(TimeSpan.FromHours(21));                 // срок прошёл
        await Raise();
        Assert.Equal([AlertKind.BatchExpired, AlertKind.BatchExpiring], (await Mine()).Select(a => a.Kind));

        (await Api.PostAsync("/api/alerts/read", null)).EnsureSuccessStatusCode();
        Assert.All(await Mine(), a => Assert.True(a.Read));
    }

    [Fact]
    public async Task Стоп_блюда_и_возврат_в_продажу_дают_сигналы()
    {
        var product = await Product();
        var dish = await Dish((product, 0.5m));
        async Task Track()
        {
            await using var scope = fx.DemoScope();
            await scope.ServiceProvider.GetRequiredService<StopTracker>().TrackAsync(default);
        }

        await Track();
        await Supply(new SupplyItemRequest(product.Id, 5, null));
        await Track();

        var kinds = (await Get<AlertsResponse>("/api/alerts?take=100")).Items.Where(a => a.Title.StartsWith(dish.Name)).Select(a => a.Kind);
        Assert.Equal([AlertKind.DishBack, AlertKind.DishStopped], kinds);
    }

    [Fact]
    public async Task Поток_событий_сообщает_об_изменении_после_заказа()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        await Supply(new SupplyItemRequest(product.Id, 1, null));
        var token = Api.DefaultRequestHeaders.Authorization!.Parameter;

        // Токен идёт в адресе: так поток открывает браузер.
        using var client = fx.CreateClient();
        using var stream = await client.GetStreamAsync($"/api/events?access_token={token}");
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        (await Order(dish)).EnsureSuccessStatusCode();

        string? line;
        do line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        while (line != "data: changed");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.CreateClient().GetAsync("/api/events")).StatusCode);
    }

    [Fact]
    public async Task Генератор_включается_только_с_правами_и_проверяет_настройки()
    {
        using var cook = await fx.SignInAsync("cook");

        var started = await Api.PutAsJsonAsync("/api/simulator", new SimulatorSettings(true, 12));
        var tooFast = await Api.PutAsJsonAsync("/api/simulator", new SimulatorSettings(true, 500));
        var byCook = await cook.PutAsJsonAsync("/api/simulator", new SimulatorSettings(true, 6));
        await Api.PutAsJsonAsync("/api/simulator", new SimulatorSettings(false, 6));

        Assert.Equal(new SimulatorSettings(true, 12), await Read<SimulatorSettings>(started));
        Assert.Equal(HttpStatusCode.BadRequest, tooFast.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byCook.StatusCode);
    }
}
