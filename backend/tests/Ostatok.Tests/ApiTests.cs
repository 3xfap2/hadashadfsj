using Microsoft.EntityFrameworkCore;
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

        using var manager = await fx.SignInAsSupportAsync();
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
        // Норматив заготовки — 48 часов, но сливки, которые в неё ушли, годны ещё 10: заготовка не переживает сырьё.
        Assert.Equal(fx.Time.GetUtcNow().AddHours(10), response.ExpiresAt);
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

        var summary = await Get<OrdersSummary>("/api/orders/summary");
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/api/orders/summary?since=2026-01-01T00:00:00%2B03:00")).StatusCode);
        Assert.Equal(325 * 2 + 500, summary.Tables.Single(t => t.Place == "Стол 7").Total);
        Assert.True(summary.Orders >= 1 && summary.Revenue >= 1150);
        // Пока за столом открыт заказ, убрать этот стол из зала нельзя.
        var manager = await fx.SignInAsync("manager");
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync("/api/restaurant", new RestaurantSettings("Демо", "10:00", "23:00", 3, true))).StatusCode);
        // Продукт уже в поставке и техкарте: единицу измерения сменить нельзя.
        var unit = await Api.PutAsJsonAsync($"/api/products/{product.Id}", new ProductRequest(product.Name, Unit.L, product.ShelfLifeHours, product.Cost!.Value), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, unit.StatusCode);
        // Логин принимается с заглавной буквы и с пробелом в конце.
        await fx.SignInAsync(" Waiter ");

        var closed = await fx.Waiter.PostAsJsonAsync("/api/orders/close", new CloseTableRequest("Стол 7"));
        var again = await fx.Waiter.PostAsJsonAsync("/api/orders/close", new CloseTableRequest("Стол 7"));

        Assert.True((await Read<CloseTableResponse>(closed)).Total >= 1150);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.NotNull((await Get<List<OrderResponse>>("/api/orders?take=200")).Single(o => o.Id == discounted.Id).ClosedAt);
        Assert.DoesNotContain((await Get<OrdersSummary>("/api/orders/summary")).Tables, t => t.Place == "Стол 7");
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
    public async Task Личные_данные_посетителей_лежат_в_базе_зашифрованными()
    {
        using var visitor = fx.CreateClient();
        var contact = $"secret{Guid.NewGuid():N}@example.com";
        await visitor.PostAsJsonAsync("/api/leads", new LeadRequest("Анна Тайная", "Кафе", contact, "1", "Перезвоните после шести"));
        var chat = await Read<ChatResponse>(await visitor.PostAsJsonAsync("/api/chats", new ChatStartRequest("Анна Тайная", contact, "Сколько стоит подключение?")));

        await using var scope = fx.DemoScope();
        var db = scope.ServiceProvider.GetRequiredService<Ostatok.Api.Data.AppDbContext>();
        // Сырые значения из базы, в обход расшифровки.
        var raw = await db.Database
            .SqlQuery<string>($"""
                SELECT "Contact" || ' ' || "Name" || ' ' || "Organization" || ' ' || COALESCE("Comment", '') AS "Value" FROM "Leads"
                UNION ALL SELECT "Contact" || ' ' || "Name" AS "Value" FROM "Chats"
                UNION ALL SELECT "Text" AS "Value" FROM "ChatMessages"
                """)
            .ToListAsync();

        Assert.NotEmpty(raw);
        Assert.All(raw, value => Assert.StartsWith("v1:", value));
        Assert.DoesNotContain(raw, value => value.Contains(contact) || value.Contains("Анна") || value.Contains("подключение"));
        // Через API те же данные читаются как обычно.
        using var manager = await fx.SignInAsSupportAsync();
        Assert.Contains((await manager.GetFromJsonAsync<List<LeadResponse>>("/api/leads", ApiFixture.Json))!, l => l.Contact == contact && l.Name == "Анна Тайная");
        Assert.Equal(contact, (await manager.GetFromJsonAsync<ChatDetails>($"/api/support/chats/{chat.Id}", ApiFixture.Json))!.Contact);
    }

    [Fact]
    public async Task Бессмысленные_значения_отклоняются_как_ошибка_запроса_а_не_сбой_сервера()
    {
        var product = await Product();
        var dish = await Dish((product, 0.1m));
        Task<HttpResponseMessage> Raw(HttpClient client, string url, string json) =>
            client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        HttpResponseMessage[] responses =
        [
            await Raw(fx.Waiter, "/api/orders", $$"""{"items":[{"dishId":"{{dish.Id}}","quantity":-5}]}"""),
            await Raw(fx.Waiter, "/api/orders", $$"""{"items":[{"dishId":"{{dish.Id}}","quantity":0.5}]}"""),
            await Raw(fx.Waiter, "/api/orders", """{"items":[{"dishId":"не идентификатор","quantity":1}]}"""),
            await Raw(fx.Waiter, "/api/orders", """{"items":["""),
            await Raw(Api, "/api/supplies", $$"""{"number":"П-минус","items":[{"productId":"{{product.Id}}","quantity":-3}]}"""),
            await Raw(Api, "/api/supplies", $$"""{"number":"П-много","items":[{"productId":"{{product.Id}}","quantity":1000000000000}]}"""),
            await Raw(Api, "/api/supplies", $$"""{"number":"П-срок","items":[{"productId":"{{product.Id}}","quantity":1,"shelfLifeHours":-5}]}"""),
            await Raw(Api, "/api/products", """{"name":"Минус","unit":"Kg","shelfLifeHours":24,"cost":-100}"""),
            await Raw(Api, "/api/products", """{"name":"Дорого","unit":"Kg","shelfLifeHours":24,"cost":99999999999999}"""),
            await Raw(Api, "/api/dishes", $$"""{"name":"Минус","price":-100,"recipe":[{"productId":"{{product.Id}}","quantity":0.1}]}"""),
            await Raw(Api, "/api/preps", $$"""{"productId":"{{product.Id}}","quantity":-2}"""),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Empty(await Batches(product));
    }

    [Fact]
    public async Task Открытый_управляющий_демо_не_читает_заявки_и_переписку()
    {
        using var manager = await fx.SignInAsync("manager");

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/support/chats")).StatusCode);
        // И сбросить пароль закрытой записи через «Сотрудников» он не может.
        var support = (await manager.GetFromJsonAsync<List<StaffResponse>>("/api/users", ApiFixture.Json))!.Single(u => u.Login == "support");
        Assert.Equal(HttpStatusCode.Conflict,
            (await manager.PutAsJsonAsync($"/api/users/{support.Id}", new StaffUpdate("Поддержка", Role.Manager, "hijacked-pass-1"), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Количество_меньше_тысячной_не_принимается_и_не_ломает_стоп_лист()
    {
        var product = await Product();

        var dish = await Api.PostAsJsonAsync("/api/dishes", new DishRequest($"Блюдо {Guid.NewGuid():N}", 100, [new RecipeItemDto(product.Id, 0.0004m)]));
        var prep = await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(product.Id, 0.0004m)]), ApiFixture.Json);
        var supply = await Api.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 0.0004m, null)]));

        Assert.All([dish, prep, supply], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/api/stop-list")).StatusCode);
    }

    [Fact]
    public async Task Номера_заготовок_выдаёт_счётчик_и_занять_их_поставкой_нельзя()
    {
        var raw = await Product(shelfLifeHours: 240);
        var sauce = await Read<ProductResponse>(await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(raw.Id, 0.01m)]), ApiFixture.Json));
        await Supply(new SupplyItemRequest(raw.Id, 10, null));

        // Поставку с номером «как у заготовки» система не принимает: иначе счётчик упёрся бы в занятый номер.
        var taken = await Api.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, "З-9999", null, [new SupplyItemRequest(raw.Id, 1, null)]));
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);

        // Двенадцать заготовок разом: все приняты, номера разные.
        var made = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Api.PostAsJsonAsync("/api/preps", new PrepRequest(sauce.Id, 1))));
        Assert.All(made, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new List<string>();
        foreach (var response in made) numbers.Add((await Read<PrepResponse>(response)).SupplyNumber);
        Assert.Equal(12, numbers.Distinct().Count());

        // Две одинаковые поставки разом: одна принята, вторая получает понятный отказ, а не сбой сервера.
        var number = $"П-{Guid.NewGuid():N}";
        var twins = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Api.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, number, null, [new SupplyItemRequest(raw.Id, 1, null)]))));
        Assert.Single(twins, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(twins, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
    }

    [Fact]
    public async Task Скидка_действует_только_на_истекающие_порции_а_сумма_заказа_не_зависит_от_новой_цены()
    {
        var product = await Product(shelfLifeHours: 240);
        var dish = await Dish((product, 1m));
        // 1 кг истекает через 2 часа, ещё 50 кг свежие: спасать нужно одну порцию.
        await Supply(new SupplyItemRequest(product.Id, 1, 2), new SupplyItemRequest(product.Id, 50, null));
        var offer = (await Get<List<HotOffer>>("/api/hot-offers")).Single(o => o.DishId == dish.Id);
        Assert.Equal(1, offer.Portions);

        // Скидку просят на все 20 порций и ещё отдельной строкой — действует она только на одну.
        var order = await Read<OrderResponse>(await fx.Waiter.PostAsJsonAsync("/api/orders", new OrderRequest(
            [new OrderLine(dish.Id, 15, Discount: true), new OrderLine(dish.Id, 5, Discount: true)])));
        var item = order.Items.Single();
        Assert.Equal((20, 1, offer.Discount!.Percent), (item.Quantity, item.DiscountQuantity, item.DiscountPercent));
        Assert.Equal(500 * 19 + offer.Discount.Price, order.Total);

        // Блюдо подорожало — сумма уже оформленного заказа не меняется.
        await Api.PutAsJsonAsync($"/api/dishes/{dish.Id}", new DishRequest($"Блюдо {Guid.NewGuid():N}", 9000, [new RecipeItemDto(product.Id, 1m)]));
        Assert.Equal(order.Total, (await Get<List<OrderResponse>>("/api/orders?take=200")).Single(o => o.Id == order.Id).Total);
    }

    [Fact]
    public async Task Прогноз_стопа_не_падает_на_запасе_на_годы_вперёд()
    {
        var product = await Product(shelfLifeHours: 100_000);
        var dish = await Dish((product, 0.001m));
        await Supply(new SupplyItemRequest(product.Id, 5000, null));
        (await Order(dish)).EnsureSuccessStatusCode();

        Assert.Null((await StopListRow(dish)).StopForecastAt);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/api/purchase-suggestions")).StatusCode);
    }

    [Fact]
    public async Task Событие_об_изменении_получает_только_свой_ресторан()
    {
        var (neighbours, _, _) = await fx.NewRestaurantAsync("Соседи");
        var other = neighbours.DefaultRequestHeaders.Authorization!.Parameter!;
        using var listener = fx.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stream = await listener.GetStreamAsync($"/api/events?access_token={other}", cts.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync(cts.Token));

        // Изменение в демо-ресторане соседям не приходит; их собственное — приходит.
        await Product();
        using var owner = fx.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new("Bearer", other);
        await owner.PostAsJsonAsync("/api/products", new ProductRequest("Свой продукт", Unit.Kg, 24, 10), ApiFixture.Json);

        var received = new List<string>();
        while (received.Count(l => l.StartsWith("data:")) == 0)
            received.Add(await reader.ReadLineAsync(cts.Token) ?? "");
        // Первое же событие — от собственного изменения: чужое до соседей не дошло бы раньше него только случайно,
        // поэтому проверяем число событий после паузы.
        await Task.Delay(300);
        Assert.Single(received, l => l.StartsWith("data:"));
    }

    [Fact]
    public async Task Отчёт_по_поставщикам_считает_брак_порчу_и_просрочку_по_журналу()
    {
        var supplier = $"Поставщик {Guid.NewGuid():N}";
        var product = await Product(shelfLifeHours: 48);
        var dish = await Dish((product, 1m));
        var response = await Api.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null,
            [new SupplyItemRequest(product.Id, 4, null), new SupplyItemRequest(product.Id, 3, null), new SupplyItemRequest(product.Id, 3, 24)], supplier));
        var batches = (await Read<SupplyResponse>(response)).Items.Select(i => i.BatchId).ToList();

        // Третья партия (срок короче) продаётся первой; первую бракуют, вторую списывают как порчу.
        (await Order(dish, 2)).EnsureSuccessStatusCode();
        await Api.PostAsJsonAsync($"/api/stock/batches/{batches[0]}/write-off", new WriteOffRequest(WriteOffCause.Defective), ApiFixture.Json);
        await Api.PostAsJsonAsync($"/api/stock/batches/{batches[1]}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json);

        var row = (await Get<SupplierReportResponse>("/api/analytics/suppliers?days=7")).Suppliers.Single(s => s.Supplier == supplier);

        Assert.Equal((1, 3), (row.Supplies, row.Batches));
        Assert.Equal((10_000m, 2000m, 4000m, 3000m, 1000m), (row.Delivered, row.Sold, row.Defective, row.Spoiled, row.InStock));
        Assert.Equal((70m, SupplierReport.Problem), (row.LossPercent, row.Verdict));
        // Отчёт закрыт для ролей без аналитики.
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Waiter.GetAsync("/api/analytics/suppliers")).StatusCode);
    }

    [Fact]
    public async Task Поддержка_в_Telegram_получает_заявки_отвечает_посетителю_и_создаёт_ресторан()
    {
        long operatorChat = Random.Shared.NextInt64(1, long.MaxValue), stranger = Random.Shared.NextInt64(1, long.MaxValue);
        async Task Say(long chat, string text, string? replied = null)
        {
            await using var scope = fx.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SupportDesk>().HandleAsync(new TelegramUpdate(1, chat, "Оператор", text, replied), default);
        }
        async Task<string[]> Received(long chat)
        {
            // Уведомления уходят в фоне — даём им долететь.
            for (var i = 0; i < 40 && fx.Telegram.To(chat).Length == 0; i++) await Task.Delay(50);
            return fx.Telegram.To(chat);
        }

        // Без пароля бот никого не подключает и на команды постороннего не отвечает.
        await Say(stranger, "/start");
        await Say(stranger, "/start not-the-password");
        await Say(stranger, "/new Чужой ресторан; stranger.one");
        Assert.All(fx.Telegram.To(stranger), m => Assert.Contains("/start <пароль поддержки>", m));
        Assert.Equal(2, fx.Telegram.To(stranger).Length);

        await Say(operatorChat, $"/start {ApiFixture.SupportPassword}");
        Assert.Contains("будут приходить сюда", fx.Telegram.To(operatorChat)[0]);

        // Заявка и вопрос с лендинга приходят оператору, постороннему — нет.
        using var visitor = fx.CreateClient();
        var organization = $"Ресторан {Guid.NewGuid():N}";
        await visitor.PostAsJsonAsync("/api/leads", new LeadRequest("Иван", organization, "ivan@example.com", "2–5", "Есть касса"));
        var chat = await Read<ChatResponse>(await visitor.PostAsJsonAsync("/api/chats", new ChatStartRequest("Мария", "+7 900 000-00-00", "Работаете с iiko?")));
        for (var i = 0; i < 40 && fx.Telegram.To(operatorChat).Length < 3; i++) await Task.Delay(50);
        var inbox = fx.Telegram.To(operatorChat);
        Assert.Contains(inbox, m => m.Contains("Новая заявка") && m.Contains(organization) && m.Contains("ivan@example.com"));
        var question = Assert.Single(inbox, m => m.Contains("Работаете с iiko?"));
        Assert.Equal(2, fx.Telegram.To(stranger).Length);

        // Ответ на сообщение с вопросом уходит посетителю в чат на сайте.
        await Say(operatorChat, "Да, через открытый API.", replied: question);
        var forVisitor = (await visitor.GetFromJsonAsync<ChatResponse>($"/api/chats/{chat.Id}", ApiFixture.Json))!;
        Assert.Equal(("Да, через открытый API.", true), (forVisitor.Messages[^1].Text, forVisitor.Messages[^1].FromStaff));
        // Посторонний, подделав «ответ» с номером диалога, посетителю написать не может.
        await Say(stranger, "Взлом", replied: question);
        Assert.Equal(2, (await visitor.GetFromJsonAsync<ChatResponse>($"/api/chats/{chat.Id}", ApiFixture.Json))!.Messages.Count);

        // Командой создаётся ресторан: бот возвращает логин и пароль, по ним можно войти.
        var login = $"t{Guid.NewGuid():N}"[..20];
        await Say(operatorChat, $"/new Кафе у моста; {login}");
        var created = fx.Telegram.To(operatorChat)[^1];
        Assert.Contains("Ресторан «Кафе у моста» создан", created);
        var password = System.Text.RegularExpressions.Regex.Match(created, @"Пароль: (\S+)").Groups[1].Value;
        using var owner = await fx.SignInAsync(login, password);
        Assert.Equal("Кафе у моста", (await owner.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!.Restaurant);

        await Say(operatorChat, "/stop");
        var before = fx.Telegram.To(operatorChat).Length;
        await visitor.PostAsJsonAsync("/api/leads", new LeadRequest("Пётр", organization, "petr@example.com", "1", null));
        await Task.Delay(300);
        Assert.Equal(before, fx.Telegram.To(operatorChat).Length);
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
        using var manager = await fx.SignInAsSupportAsync();
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
        Assert.Equal(["today", "menu", "board", "kitchen"], (await cook.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!.Sections);
    }

    [Fact]
    public async Task Новый_ресторан_работает_в_своём_пространстве_и_не_видит_чужих_данных()
    {
        // В демо-ресторане есть продукт, блюдо и партия.
        var theirs = await Product();
        var theirDish = await Dish((theirs, 0.1m));
        var theirSupply = await Supply(new SupplyItemRequest(theirs.Id, 3, null));

        using var anonymous = fx.CreateClient();
        // Ресторан создаёт поддержка; посетитель сайта и управляющий другого ресторана этого сделать не могут.
        var (owner, login, _) = await fx.NewRestaurantAsync("Траттория");
        using var support = await fx.SignInAsSupportAsync();
        var taken = await support.PostAsJsonAsync("/api/restaurants", new RestaurantCreateRequest("Двойник", login));
        var byVisitor = await anonymous.PostAsJsonAsync("/api/restaurants", new RestaurantCreateRequest("Сам себе", "self.made"));
        var byOtherManager = await owner.PostAsJsonAsync("/api/restaurants", new RestaurantCreateRequest("Филиал", "branch.one"));
        var oldRegistration = await anonymous.PostAsJsonAsync("/api/auth/register", new { restaurant = "Открытая", login = "open.door", password = "open-door-2026" });
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.MethodNotAllowed),
            (taken.StatusCode, byVisitor.StatusCode, byOtherManager.StatusCode, oldRegistration.StatusCode));

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
        // У ресторана своя нумерация заказов и свой зал.
        Assert.Equal(1, (await Read<OrderResponse>(own)).Number);
        var hall = await owner.PutAsJsonAsync("/api/restaurant", new RestaurantSettings("Траттория на углу", "10:00", "23:00", 3, false));
        Assert.Equal(HttpStatusCode.OK, hall.StatusCode);
        var renamed = (await owner.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!;
        Assert.Equal(("Траттория на углу", "10:00"), (renamed.Restaurant, renamed.Shift.Start));
        Assert.Equal(["Стол 1", "Стол 2", "Стол 3"], renamed.Places);
        Assert.Equal(HttpStatusCode.BadRequest, (await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)], "Стол 7"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/restaurant", new RestaurantSettings("Траттория", "23:00", "23:00", 3, false))).StatusCode);
        Assert.Equal(0.6m, (await owner.GetFromJsonAsync<List<BatchRow>>("/api/stock/batches", ApiFixture.Json))!.Single().Remaining);

        // Удалённый сотрудник теряет доступ сразу, а не когда истечёт его токен.
        var temp = await Read<StaffResponse>(await owner.PostAsJsonAsync("/api/users", new StaffRequest($"t{login}"[..20], "temp-2026-pw", "Стажёр", Role.Cook), ApiFixture.Json));
        using var fired = fx.CreateClient();
        fired.DefaultRequestHeaders.Authorization = new("Bearer",
            (await Read<LoginResponse>(await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(temp.Login, "temp-2026-pw")))).Token);
        Assert.Equal(HttpStatusCode.OK, (await fired.GetAsync("/api/stop-list")).StatusCode);
        await owner.DeleteAsync($"/api/users/{temp.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fired.GetAsync("/api/stop-list")).StatusCode);
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
        // Токен, выданный до смены роли и пароля, больше не действует.
        Assert.Equal(HttpStatusCode.Unauthorized, (await waiter.GetAsync("/api/stop-list")).StatusCode);
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
        // Управляющему, кроме склада и аналитики, доступны сотрудники. Обращения с лендинга открытый
        // управляющий демо не видит: его пароль опубликован.
        using var support = await fx.SignInAsSupportAsync();
        Assert.Contains("support", (await support.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!.Sections);
        using var manager = await fx.SignInAsync("manager");
        Assert.Equal(["today", "stock", "menu", "board", "kitchen", "purchasing", "analytics", "catalog", "staff"],
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

    [Fact]
    public async Task Поставка_извне_попадает_на_склад_только_после_подтверждения()
    {
        var product = await Product(shelfLifeHours: 48);
        var request = new SupplyRequest(Guid.NewGuid(), $"ВН-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 3, null)], "Поставщик");

        var sent = await Api.PostAsJsonAsync("/api/supplies/incoming", request);
        var twice = await Api.PostAsJsonAsync("/api/supplies/incoming", request);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Conflict), (sent.StatusCode, twice.StatusCode));
        // Пока поставку не подтвердили, на складе её нет, а в кабинете есть сигнал.
        Assert.Empty(await Batches(product));
        Assert.Contains(await Get<List<IncomingResponse>>("/api/supplies/incoming"), s => s.Id == request.Id && s.Total == 3000);
        Assert.Contains((await Get<AlertsResponse>("/api/alerts?take=100")).Items, a => a.Kind == AlertKind.SupplyWaiting && a.Title.Contains(request.Number!));
        // Чужой ресторан эту поставку не видит и принять не может.
        var (stranger, _, _) = await fx.NewRestaurantAsync("Чужой склад");
        Assert.Empty((await stranger.GetFromJsonAsync<List<IncomingResponse>>("/api/supplies/incoming", ApiFixture.Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/supplies/incoming/{request.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Waiter.PostAsync($"/api/supplies/incoming/{request.Id}/accept", null)).StatusCode);

        var accepted = await Api.PostAsync($"/api/supplies/incoming/{request.Id}/accept", null);
        var again = await Api.PostAsync($"/api/supplies/incoming/{request.Id}/accept", null);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NotFound), (accepted.StatusCode, again.StatusCode));
        Assert.Equal(3, (await Batches(product)).Single().Remaining);
        Assert.DoesNotContain(await Get<List<IncomingResponse>>("/api/supplies/incoming"), s => s.Id == request.Id);

        // Отклонённая поставка на склад не попадает.
        var other = request with { Id = Guid.NewGuid(), Number = $"ВН-{Guid.NewGuid():N}" };
        await Api.PostAsJsonAsync("/api/supplies/incoming", other);
        Assert.Equal(HttpStatusCode.NoContent, (await Api.PostAsync($"/api/supplies/incoming/{other.Id}/reject", null)).StatusCode);
        Assert.Equal(3, (await Batches(product)).Sum(b => b.Remaining));
        Assert.DoesNotContain(await Get<List<IncomingResponse>>("/api/supplies/incoming"), s => s.Id == other.Id);
    }

    [Fact]
    public async Task Вскрытая_часть_партии_получает_короткий_срок_и_уходит_первой()
    {
        var created = await Api.PostAsJsonAsync("/api/products",
            new ProductRequest($"Сливки {Guid.NewGuid():N}", Unit.L, 120, 500, OpenedShelfLifeHours: 24), ApiFixture.Json);
        var product = await Read<ProductResponse>(created);
        var dish = await Dish((product, 0.5m));
        await Supply(new SupplyItemRequest(product.Id, 5, null));
        var sealedBatch = (await Batches(product)).Single();

        var tooMuch = await Api.PostAsJsonAsync($"/api/stock/batches/{sealedBatch.Id}/open", new OpenBatchRequest(6));
        var opened = await Api.PostAsJsonAsync($"/api/stock/batches/{sealedBatch.Id}/open", new OpenBatchRequest(1));
        var byWaiter = await fx.Waiter.PostAsJsonAsync($"/api/stock/batches/{sealedBatch.Id}/open", new OpenBatchRequest(1));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.Forbidden), (tooMuch.StatusCode, opened.StatusCode, byWaiter.StatusCode));
        var batches = await Batches(product);
        var open = batches.Single(b => b.OpenedAt is not null);
        // Литр вскрыт и годен сутки, четыре литра лежат закрытыми с прежним сроком; всего по-прежнему пять.
        Assert.Equal((1m, 4m), (open.Remaining, batches.Single(b => b.OpenedAt is null).Remaining));
        Assert.InRange(open.HoursLeft, 23, 24);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.PostAsJsonAsync($"/api/stock/batches/{open.Id}/open", new OpenBatchRequest(null))).StatusCode);

        // Заказ списывает сначала вскрытое.
        (await Order(dish)).EnsureSuccessStatusCode();
        Assert.Equal(0.5m, (await Batches(product)).Single(b => b.OpenedAt is not null).Remaining);

        // Без срока после вскрытия партию вскрыть нельзя.
        var plain = await Product();
        await Supply(new SupplyItemRequest(plain.Id, 1, null));
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.PostAsJsonAsync($"/api/stock/batches/{(await Batches(plain)).Single().Id}/open", new OpenBatchRequest(null))).StatusCode);
    }

    [Fact]
    public async Task Инвентаризация_исправляет_остатки_и_пишет_расхождения_в_журнал()
    {
        var less = await Product();
        var more = await Product();
        var same = await Product();
        var absent = await Product();
        // У первого продукта две партии: недостача уходит из той, что истекает раньше.
        await Supply(new SupplyItemRequest(less.Id, 2, 10), new SupplyItemRequest(more.Id, 3, null), new SupplyItemRequest(same.Id, 1, null));
        await Supply(new SupplyItemRequest(less.Id, 4, 48));

        var noBatch = await Api.PostAsJsonAsync("/api/stock/inventory", new InventoryRequest([new InventoryLine(absent.Id, 1)]));
        var counted = await Api.PostAsJsonAsync("/api/stock/inventory",
            new InventoryRequest([new InventoryLine(less.Id, 3.5m), new InventoryLine(more.Id, 3.4m), new InventoryLine(same.Id, 1)]));
        var byWaiter = await fx.Waiter.PostAsJsonAsync("/api/stock/inventory", new InventoryRequest([new InventoryLine(same.Id, 0)]));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.Forbidden), (noBatch.StatusCode, counted.StatusCode, byWaiter.StatusCode));
        var result = await Read<InventoryResponse>(counted);
        // Цена продукта 1000 ₽: не хватило 2,5 кг, нашлось лишних 0,4 кг; продукт без расхождения в итог не попал.
        Assert.Equal((2500m, 400m), (result.Shortage, result.Surplus));
        Assert.Equal([less.Id, more.Id], result.Rows.Select(r => r.ProductId));
        Assert.Equal([4m - 0.5m], (await Batches(less)).Select(b => b.Remaining));
        Assert.Equal(3.4m, (await Batches(more)).Single().Remaining);
        var journal = (await Get<List<MovementRow>>("/api/movements?take=20")).Where(m => m.Note == InventoryEndpoints.Note).ToList();
        Assert.Equal(2.5m, journal.Where(m => m.Type == MovementType.Shortage && m.ProductId == less.Id).Sum(m => m.Quantity));
        Assert.Equal(0.4m, journal.Single(m => m.Type == MovementType.Surplus).Quantity);
    }

    [Fact]
    public async Task Меню_для_гостя_открыто_без_входа_и_не_показывает_блюда_из_стопа()
    {
        var product = await Product();
        var empty = await Product();
        var available = await Dish((product, 0.2m));
        var stopped = await Dish((empty, 0.2m));
        await Supply(new SupplyItemRequest(product.Id, 2, 4));
        using var guest = fx.CreateClient();

        var menu = (await guest.GetFromJsonAsync<GuestMenu>($"/api/public/menu/{Restaurant.DemoId}", ApiFixture.Json))!;
        var missing = await guest.GetAsync($"/api/public/menu/{Guid.NewGuid()}");
        var (_, _, _) = await fx.NewRestaurantAsync("Соседи");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain(menu.Dishes, d => d.DishId == stopped.Id);
        // Продукт истекает через 4 часа: блюдо попало в предложения дня, гость видит цену со скидкой (500 → 325).
        Assert.Equal((500m, 325m, 35), menu.Dishes.Where(d => d.DishId == available.Id).Select(d => (d.Price, d.OfferPrice!.Value, d.OfferPercent!.Value)).Single());
    }

    private static async Task<T> Get<T>(HttpClient client, string url) => (await client.GetFromJsonAsync<T>(url, ApiFixture.Json))!;

    /// <summary>Сотрудник нового ресторана: управляющий заводит его, и он входит под своим паролем.</summary>
    private async Task<HttpClient> Staff(HttpClient owner, Role role)
    {
        var login = $"s{Guid.NewGuid():N}"[..20];
        (await owner.PostAsJsonAsync("/api/users", new StaffRequest(login, "staff-2026-pw", role.ToString(), role), ApiFixture.Json)).EnsureSuccessStatusCode();
        return await fx.SignInAsync(login, "staff-2026-pw");
    }

    /// <summary>Продукт по 500 ₽ в ресторане управляющего.</summary>
    private async Task<ProductResponse> Product(HttpClient owner) =>
        await Read<ProductResponse>(await owner.PostAsJsonAsync("/api/products", new ProductRequest($"Продукт {Guid.NewGuid():N}", Unit.Kg, 48, 500), ApiFixture.Json));

    private async Task<SupplyResponse> Supply(HttpClient owner, params SupplyItemRequest[] items)
    {
        var response = await owner.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, items.ToList()));
        response.EnsureSuccessStatusCode();
        return await Read<SupplyResponse>(response);
    }

    [Fact]
    public async Task Цена_заготовки_пересчитывается_когда_меняется_цена_сырья()
    {
        var cream = await Product();
        var cheese = await Product();
        var sauce = await Read<ProductResponse>(await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(cream.Id, 0.5m), new RecipeItemDto(cheese.Id, 0.2m)]), ApiFixture.Json));
        Assert.Equal(700, sauce.Cost);

        var update = await Api.PutAsJsonAsync($"/api/products/{cream.Id}", new ProductRequest(cream.Name, Unit.Kg, cream.ShelfLifeHours, 1400), ApiFixture.Json);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        // 0,5 × 1400 + 0,2 × 1000: сливки подорожали — подорожал и соус.
        Assert.Equal(900, (await Get<List<ProductResponse>>("/api/products")).Single(p => p.Id == sauce.Id).Cost);
    }

    [Fact]
    public async Task Цена_из_файла_поставки_становится_закупочной_ценой_продукта()
    {
        var cream = await Product();
        var sauce = await Read<ProductResponse>(await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(cream.Id, 0.5m)]), ApiFixture.Json));
        async Task<decimal?> Cost(ProductResponse product) => (await Get<List<ProductResponse>>("/api/products")).Single(p => p.Id == product.Id).Cost;
        // В файле — сумма по строке: 4 кг на 4 800 ₽, то есть 1 200 ₽ за килограмм при цене в справочнике 1 000 ₽.
        var csv = $"продукт;количество;единица;срок хранения;цена\n{cream.Name};4;кг;72 ч;4800\n{sauce.Name};1;л;24 ч;999\n{cream.Name};1;кг;72 ч;";
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));

        var rows = await Read<List<ImportRow>>(await Api.PostAsync("/api/supplies/import/preview", content));

        // Предпросмотр показывает, как изменится цена, но сам её не трогает. У заготовки цена своя, без суммы в файле цены нет.
        Assert.Equal((1200m, 1000m), (rows[0].Cost!.Value, rows[0].CurrentCost!.Value));
        Assert.Equal((null, null), (rows[1].Cost, rows[2].Cost));
        Assert.Equal(1000, await Cost(cream));

        var supply = await Supply(new SupplyItemRequest(cream.Id, 4, 72, rows[0].Cost));

        Assert.Equal(4800, supply.Total);
        Assert.Equal(1200, await Cost(cream));
        Assert.Equal(600, await Cost(sauce));
        // Поставка, введённая вручную, цену не передаёт и справочник не меняет; нулевая цена не принимается.
        await Supply(new SupplyItemRequest(cream.Id, 1, null));
        var free = await Api.PostAsJsonAsync("/api/supplies", new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(cream.Id, 1, null, 0)]));
        Assert.Equal(HttpStatusCode.BadRequest, free.StatusCode);
        Assert.Equal(1200, await Cost(cream));
    }

    [Fact]
    public async Task Инвентаризация_сохраняется_документом_с_номером_и_попадает_в_потери()
    {
        var (owner, login, _) = await fx.NewRestaurantAsync("Учёт");
        var (less, more, same) = (await Product(owner), await Product(owner), await Product(owner));
        var spoiled = await Supply(owner, new SupplyItemRequest(same.Id, 1, null));
        await Supply(owner, new SupplyItemRequest(less.Id, 2, null), new SupplyItemRequest(more.Id, 3, null), new SupplyItemRequest(same.Id, 1, null));
        (await owner.PostAsJsonAsync($"/api/stock/batches/{spoiled.Items.Single().BatchId}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json)).EnsureSuccessStatusCode();

        // Излишек без годной партии записать некуда: такой пересчёт отклоняется и номер не занимает.
        var absent = await Product(owner);
        var failed = await owner.PostAsJsonAsync("/api/stock/inventory", new InventoryRequest([new InventoryLine(absent.Id, 1)]));
        var first = await Read<InventoryResponse>(await owner.PostAsJsonAsync("/api/stock/inventory",
            new InventoryRequest([new InventoryLine(less.Id, 1.5m), new InventoryLine(more.Id, 3.4m), new InventoryLine(same.Id, 1)])));
        var second = await Read<InventoryResponse>(await owner.PostAsJsonAsync("/api/stock/inventory", new InventoryRequest([new InventoryLine(same.Id, 1)])));

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Equal((1, 2), (first.Number, second.Number));
        var list = await Get<List<InventorySummary>>(owner, "/api/stock/inventories");
        Assert.Equal([second.Id, first.Id], list.Select(i => i.Id));
        // Цена продукта 500 ₽: не хватило 0,5 кг, нашлось лишних 0,4 кг.
        Assert.Equal((login, fx.Time.GetUtcNow(), 3, 2, 250m, 200m), (list[1].Actor, list[1].At, list[1].Counted, list[1].Differences, list[1].Shortage, list[1].Surplus));
        var doc = await Get<InventoryResponse>(owner, $"/api/stock/inventories/{first.Id}");
        // В документе все пересчитанные продукты, в том числе тот, что сошёлся.
        Assert.Equal([(less.Id, 2m, 1.5m, -0.5m, -250m), (same.Id, 1m, 1m, 0m, 0m), (more.Id, 3m, 3.4m, 0.4m, 200m)],
            doc.Rows.Select(r => (r.ProductId, r.Accounted, r.Actual, r.Difference, r.Value)));

        // Недостача — такая же потеря, как списание: она входит в общую сумму отдельной причиной, излишек её не уменьшает.
        var report = await Get<WriteOffReport>(owner, "/api/analytics/write-offs?days=7");
        Assert.Equal((750m, 200m), (report.Total, report.Surplus));
        Assert.Equal([(WriteOffService.SpoiledNote, 500m), (InsightsEndpoints.ShortageReason, 250m)], report.ByReason.Select(r => (r.Reason, r.Value)));
        Assert.Equal(750m, report.ByDay.Sum(d => d.Value));
        Assert.Contains(report.ByProduct, p => p.ProductId == less.Id && p.Quantity == 0.5m && p.Value == 250m);

        // Документы другого ресторана не видны, официанту раздел закрыт.
        Assert.DoesNotContain(await Get<List<InventorySummary>>("/api/stock/inventories"), i => i.Id == first.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"/api/stock/inventories/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Waiter.GetAsync("/api/stock/inventories")).StatusCode);
    }

    [Fact]
    public async Task Сигнал_прочитан_только_у_того_кто_его_прочитал()
    {
        var (owner, _, _) = await fx.NewRestaurantAsync("Сигналы");
        var product = await Product(owner);
        using var cook = await Staff(owner, Role.Cook);
        // Поставка от внешней системы поднимает сигнал «ждёт подтверждения».
        Task Signal() => owner.PostAsJsonAsync("/api/supplies/incoming", new SupplyRequest(null, $"В-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null)]));
        // Счётчик непрочитанных и сколько сигналов в списке помечены прочитанными.
        async Task<(int Unread, int Read)> Alerts(HttpClient client)
        {
            var alerts = await Get<AlertsResponse>(client, "/api/alerts");
            return (alerts.Unread, alerts.Items.Count(a => a.Read));
        }

        await Signal();
        Assert.Equal((1, 0), await Alerts(owner));
        Assert.Equal((1, 0), await Alerts(cook));

        (await owner.PostAsync("/api/alerts/read", null)).EnsureSuccessStatusCode();

        // Управляющий прочитал — у повара сигнал по-прежнему новый.
        Assert.Equal((0, 1), await Alerts(owner));
        Assert.Equal((1, 0), await Alerts(cook));

        await Signal();
        Assert.Equal((1, 1), await Alerts(owner));
        Assert.Equal((2, 0), await Alerts(cook));
        // Сотруднику, которого завели позже, прежние сигналы не показываются новыми.
        using var late = await Staff(owner, Role.Storekeeper);
        Assert.Equal((0, 2), await Alerts(late));
    }

    [Fact]
    public async Task Официант_и_повар_не_получают_закупочные_цены()
    {
        var product = await Product();
        var dish = await Dish((product, 0.2m));
        // Продукт истекает через 4 часа: блюдо попадает в горячие предложения со скидкой.
        await Supply(new SupplyItemRequest(product.Id, 2, 4));
        using var cook = await fx.SignInAsync("cook");
        using var storekeeper = await fx.SignInAsync("storekeeper");

        foreach (var client in new[] { fx.Waiter, cook })
        {
            Assert.Null((await Get<List<ProductResponse>>(client, "/api/products")).Single(p => p.Id == product.Id).Cost);
            Assert.Null((await Get<List<StopListRow>>(client, "/api/stop-list")).Single(r => r.DishId == dish.Id).Cost);
            Assert.Null((await Get<List<StockRow>>(client, "/api/stock")).Single(r => r.ProductId == product.Id).FreshValue);
            Assert.Null((await Get<List<BatchRow>>(client, $"/api/stock/batches?productId={product.Id}")).Single().Value);
            Assert.All(await Get<List<SupplyResponse>>(client, "/api/supplies?take=5"), s => Assert.Null(s.Total));
            var offer = (await Get<List<HotOffer>>(client, "/api/hot-offers")).Single(o => o.DishId == dish.Id);
            Assert.Equal((null, null, null, 0m), (offer.ValueAtRisk, offer.Ingredients[0].ValueAtRisk, offer.Discount!.FloorPrice, offer.Score));
            // Цена для гостя и размер скидки остаются: без них официант не продаст блюдо.
            Assert.Equal((500m, 35, 325m), (offer.Price, offer.Discount.Percent, offer.Discount.Price));
            Assert.DoesNotContain("продуктов, которые", offer.Reason);
            Assert.DoesNotContain("невыгодно", offer.Discount.Reason);
        }

        // Шеф-повар и кладовщик работают с деньгами склада — им цифры приходят как раньше.
        foreach (var client in new[] { Api, storekeeper })
        {
            Assert.Equal(1000, (await Get<List<ProductResponse>>(client, "/api/products")).Single(p => p.Id == product.Id).Cost);
            Assert.Equal(200, (await Get<List<StopListRow>>(client, "/api/stop-list")).Single(r => r.DishId == dish.Id).Cost);
            Assert.Equal(2000, (await Get<List<StockRow>>(client, "/api/stock")).Single(r => r.ProductId == product.Id).FreshValue);
            var offer = (await Get<List<HotOffer>>(client, "/api/hot-offers")).Single(o => o.DishId == dish.Id);
            Assert.Equal((2000m, 0m), (offer.ValueAtRisk!.Value, offer.Discount!.FloorPrice!.Value));
            Assert.Contains("невыгодно", offer.Discount.Reason);
        }
    }

    [Fact]
    public async Task Смена_может_переходить_через_полночь()
    {
        var (owner, _, _) = await fx.NewRestaurantAsync("Ночной бар");
        Task<HttpResponseMessage> Shift(string start, string end) =>
            owner.PutAsJsonAsync("/api/restaurant", new RestaurantSettings("Ночной бар", start, end, 4, true));

        // Конец раньше начала — смена до следующего дня; «9:00» принимается как «09:00».
        Assert.Equal(HttpStatusCode.OK, (await Shift("9:00", "2:30")).StatusCode);
        var me = await Get<MeResponse>(owner, "/api/me");
        Assert.Equal(("09:00", "02:30"), (me.Shift.Start, me.Shift.End));
        Assert.Equal(HttpStatusCode.OK, (await Shift("18:00", "02:00")).StatusCode);
        Assert.All([await Shift("18:00", "18:00"), await Shift("24:00", "02:00"), await Shift("18:00", "25:00"), await Shift("вечер", "02:00")],
            r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        // Отчёты с такой сменой считаются, а не падают.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/analytics/stop-cost")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/analytics/shift")).StatusCode);
    }

    [Fact]
    public async Task Отчёт_смены_собирает_продажи_потери_стопы_и_дела_на_завтра()
    {
        var zone = TimeSpan.FromHours(3);
        // Отчёт считается за сутки ресторана: сценарий начинается в полдень, чтобы не пересечь полночь.
        var local = fx.Time.GetUtcNow().ToOffset(zone);
        fx.Time.Advance(local.Date.AddHours(local.Hour < 12 ? 12 : 36) - local.DateTime);
        var today = DateOnly.FromDateTime(fx.Time.GetUtcNow().ToOffset(zone).Date);

        var (owner, _, _) = await fx.NewRestaurantAsync("Вечер");
        var me = await Get<MeResponse>(owner, "/api/me");
        using var waiter = await Staff(owner, Role.Waiter);
        var (product, empty, spare) = (await Product(owner), await Product(owner), await Product(owner));
        async Task<DishResponse> Dish(ProductResponse of) =>
            await Read<DishResponse>(await owner.PostAsJsonAsync("/api/dishes", new DishRequest($"Блюдо {Guid.NewGuid():N}", 400, [new RecipeItemDto(of.Id, 0.2m)])));
        var (dish, stopped) = (await Dish(product), await Dish(empty));
        // Партия продукта истекает завтра; запасную партию спишут как испорченную.
        await Supply(owner, new SupplyItemRequest(product.Id, 3, 20));
        var bad = await Supply(owner, new SupplyItemRequest(spare.Id, 1, null));
        async Task Track()
        {
            await using var scope = fx.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<Ostatok.Api.Data.Tenant>().Id = me.RestaurantId;
            await scope.ServiceProvider.GetRequiredService<StopTracker>().TrackAsync(default);
        }

        await Track();                                   // второе блюдо в стопе: продукта нет
        (await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 2)]))).EnsureSuccessStatusCode();
        var rejected = await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(stopped.Id, 1)]));
        (await owner.PostAsJsonAsync($"/api/stock/batches/{bad.Items.Single().BatchId}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/stock/inventory", new InventoryRequest([new InventoryLine(product.Id, 2.1m)]))).EnsureSuccessStatusCode();
        fx.Time.Advance(TimeSpan.FromHours(2));
        await Track();

        var report = await Get<ShiftReport>(owner, "/api/analytics/shift");

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal((today, true, 2, 1, 800m), (report.Date, report.Current, report.Orders, report.RejectedOrders, report.Revenue));
        Assert.Equal([(dish.Id, 2, 800m)], report.TopDishes.Select(d => (d.DishId, d.Portions, d.Revenue)));
        // Списана испорченная партия (500 ₽) и не хватило 0,5 кг по инвентаризации (250 ₽).
        Assert.Equal(750m, report.Losses);
        Assert.Equal([(WriteOffService.SpoiledNote, 500m), (InsightsEndpoints.ShortageReason, 250m)], report.WriteOffs.Select(r => (r.Reason, r.Value)));
        Assert.Equal([(stopped.Id, 2d)], report.Stops.Select(s => (s.DishId, s.StopHours)));
        Assert.Equal([(product.Id, 2.1m, 1050m)], report.Expiring!.Select(e => (e.ProductId, e.Quantity, e.Value)));
        Assert.Contains(report.ToOrder!, p => p.ProductId == empty.Id);

        // Вчерашний отчёт пуст и не подмешивает сегодняшнее состояние склада; официанту аналитика закрыта.
        var yesterday = await Get<ShiftReport>(owner, $"/api/analytics/shift?date={today.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal((false, 0, 0m, 0m), (yesterday.Current, yesterday.Orders, yesterday.Revenue, yesterday.Losses));
        Assert.Null(yesterday.Expiring);
        Assert.Null(yesterday.ToOrder);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync("/api/analytics/shift")).StatusCode);
    }

    [Fact]
    public async Task Заказ_стоит_в_очереди_кухни_пока_повар_не_отметит_его_готовым()
    {
        var (owner, _, _) = await fx.NewRestaurantAsync("Кухня");
        using var waiter = await Staff(owner, Role.Waiter);
        using var cook = await Staff(owner, Role.Cook);
        var product = await Product(owner);
        var dish = await Read<DishResponse>(await owner.PostAsJsonAsync("/api/dishes", new DishRequest("Суп дня", 400, [new RecipeItemDto(product.Id, 0.2m)])));
        await Supply(owner, new SupplyItemRequest(product.Id, 1, null));
        Task<HttpResponseMessage> Order(int quantity) => waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, quantity)], "Стол 3"));

        var old = await Read<OrderResponse>(await Order(1));
        fx.Time.Advance(TimeSpan.FromHours(7));
        var first = await Read<OrderResponse>(await Order(2));
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        var second = await Read<OrderResponse>(await Order(1));
        var rejected = await Order(50);

        // В очереди — принятые заказы последних часов, от старых к новым: забытый утренний и отклонённый в неё не попадают.
        var board = await Get<KitchenBoard>(cook, "/api/orders/kitchen");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal([first.Id, second.Id], board.Queue.Select(o => o.Id));
        Assert.Equal(("Стол 3", "Суп дня", 2, first.CreatedAt), board.Queue.First().Items.Select(i => (board.Queue.First().Place, i.DishName, i.Quantity, board.Queue.First().CreatedAt)).Single());
        Assert.Empty(board.Ready);

        // Отмечает готовность только кухня; повторная отметка время не меняет.
        var byWaiter = await waiter.PostAsync($"/api/orders/{first.Id}/ready", null);
        var ready = await cook.PostAsync($"/api/orders/{first.Id}/ready", null);
        var readyAt = fx.Time.GetUtcNow();
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        var again = await cook.PostAsync($"/api/orders/{first.Id}/ready", null);
        var unknown = await cook.PostAsync($"/api/orders/{Guid.NewGuid()}/ready", null);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NotFound),
            (byWaiter.StatusCode, ready.StatusCode, again.StatusCode, unknown.StatusCode));

        board = await Get<KitchenBoard>(cook, "/api/orders/kitchen");
        Assert.Equal([second.Id], board.Queue.Select(o => o.Id));
        Assert.Equal([(first.Id, (DateTimeOffset?)readyAt)], board.Ready.Select(o => (o.Id, o.ReadyAt)));
        // Официант видит отметку в своих заказах, а сам экран кухни ему закрыт; чужая кухня этих заказов не видит.
        var mine = await Get<List<OrderResponse>>(waiter, "/api/orders");
        Assert.Equal([(second.Id, false), (first.Id, true), (old.Id, false)], mine.Where(o => o.Status == OrderStatus.Accepted).Select(o => (o.Id, o.ReadyAt != null)));
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync("/api/orders/kitchen")).StatusCode);
        Assert.DoesNotContain((await Get<KitchenBoard>("/api/orders/kitchen")).Queue, o => o.Id == second.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync($"/api/orders/{second.Id}/ready", null)).StatusCode);

        // Стол помечается готовым, когда кухня отдала все его открытые заказы.
        async Task<bool> TableReady() => (await Get<OrdersSummary>(waiter, "/api/orders/summary")).Tables.Single(t => t.Place == "Стол 3").Ready;
        Assert.False(await TableReady());
        await cook.PostAsync($"/api/orders/{second.Id}/ready", null);
        await cook.PostAsync($"/api/orders/{old.Id}/ready", null);
        Assert.True(await TableReady());
    }

    [Fact]
    public async Task Входящую_поставку_нельзя_одновременно_принять_и_отклонить()
    {
        var product = await Product();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var request = new SupplyRequest(Guid.NewGuid(), $"ВН-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null)]);
            (await Api.PostAsJsonAsync("/api/supplies/incoming", request)).EnsureSuccessStatusCode();

            var results = await Task.WhenAll(
                Api.PostAsync($"/api/supplies/incoming/{request.Id}/accept", null),
                Api.PostAsync($"/api/supplies/incoming/{request.Id}/reject", null));

            // Успешна ровно одна из двух операций; вторая видит, что поставка уже решена.
            Assert.Single(results, r => r.IsSuccessStatusCode);
            Assert.Contains(results, r => r.StatusCode == HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Заготовка_заказ_и_инвентаризация_одновременно_не_блокируют_друг_друга()
    {
        var raw = await Product();
        var created = await Api.PostAsJsonAsync("/api/products", new ProductRequest(
            $"Соус {Guid.NewGuid():N}", Unit.L, 48, 0, [new RecipeItemDto(raw.Id, 0.01m)]), ApiFixture.Json);
        var prep = await Read<ProductResponse>(created);
        var dish = await Dish((raw, 0.01m));
        await Supply(new SupplyItemRequest(raw.Id, 50, null));

        var calls = Enumerable.Range(0, 12).SelectMany(_ => new[]
        {
            Api.PostAsJsonAsync("/api/preps", new PrepRequest(prep.Id, 0.1m)),
            Order(dish),
            Api.PostAsJsonAsync("/api/stock/inventory", new InventoryRequest([new InventoryLine(raw.Id, 40)])),
        });
        var results = await Task.WhenAll(calls);

        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);
        Assert.DoesNotContain(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Граничные_значения_не_роняют_сервер()
    {
        var product = await Product();
        using var manager = await fx.SignInAsync("manager");

        var date = await Api.GetAsync("/api/analytics/shift?date=0001-01-01");
        var digits = await manager.PutAsJsonAsync("/api/restaurant", new RestaurantSettings("Демо", "0\u0669:00", "23:00", 12, true));
        var cost = await Api.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null, 0.0004m)]));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (date.StatusCode, digits.StatusCode, cost.StatusCode));
    }

    [Fact]
    public async Task Новая_цена_продукта_не_переоценивает_уже_купленное()
    {
        var product = await Product();   // цена в справочнике — 1000 ₽
        var first = await Supply(new SupplyItemRequest(product.Id, 2, null));
        (await Api.PostAsJsonAsync($"/api/stock/batches/{(await Batches(product)).Single().Id}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json)).EnsureSuccessStatusCode();

        // Вторая поставка пришла втрое дороже: цена в справочнике обновилась.
        var second = await Read<SupplyResponse>(await Api.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null, 3000)])));

        Assert.Equal(3000, (await Get<List<ProductResponse>>("/api/products")).Single(p => p.Id == product.Id).Cost);
        var supplies = await Get<List<SupplyResponse>>("/api/supplies?take=200");
        // Первая поставка и её списание по-прежнему оцениваются по 1000 ₽, новая партия — по 3000 ₽.
        Assert.Equal((2000m, 3000m), (supplies.Single(s => s.Id == first.Id).Total, supplies.Single(s => s.Id == second.Id).Total));
        Assert.Equal(3000, (await Batches(product)).Single().Value);
        var losses = await Get<WriteOffReport>("/api/analytics/write-offs?days=1");
        Assert.Equal(2000, losses.ByProduct.Single(p => p.ProductId == product.Id).Value);
    }

    [Fact]
    public async Task Повтор_заказа_с_тем_же_идентификатором_ничего_не_списывает_второй_раз()
    {
        var product = await Product();
        var dish = await Dish((product, 0.2m));
        await Supply(new SupplyItemRequest(product.Id, 2, null));
        var request = new OrderRequest([new OrderLine(dish.Id, 1)], Id: Guid.NewGuid());

        // Официант нажал «Оформить» несколько раз подряд, пока сервер просыпался: все запросы приходят разом.
        var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fx.Waiter.PostAsJsonAsync("/api/orders", request)));
        var later = await fx.Waiter.PostAsJsonAsync("/api/orders", request);

        Assert.All(burst.Append(later), r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new HashSet<int>();
        foreach (var r in burst.Append(later)) numbers.Add((await Read<OrderResponse>(r)).Number);
        // Заказ один, списана одна порция, номера следующих заказов идут подряд.
        Assert.Single(numbers);
        Assert.Equal(1.8m, (await Batches(product)).Single().Remaining);
        Assert.Equal(numbers.Single() + 1, (await Read<OrderResponse>(await Order(dish))).Number);

        // Отклонённый заказ при повторе тоже возвращает прежний отказ, даже если продукт уже привезли.
        var big = new OrderRequest([new OrderLine(dish.Id, 100)], Id: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await fx.Waiter.PostAsJsonAsync("/api/orders", big)).StatusCode);
        await Supply(new SupplyItemRequest(product.Id, 50, null));
        Assert.Equal(HttpStatusCode.Conflict, (await fx.Waiter.PostAsJsonAsync("/api/orders", big)).StatusCode);
    }

    [Fact]
    public async Task Фото_накладной_превращается_в_строки_поставки_но_на_склад_само_не_попадает()
    {
        var product = await Product();
        fx.Invoices.Table = [["продукт", "количество", "единица", "срок хранения", "цена"], [product.Name, "2.5", "кг", "36 ч", "3000"], ["Неизвестный товар", "1", "шт", "", ""]];
        ByteArrayContent Photo() => new([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);

        var preview = await Api.PostAsync("/api/supplies/import/preview", Photo());
        var byWaiter = await fx.Waiter.PostAsync("/api/supplies/import/preview", Photo());

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Forbidden), (preview.StatusCode, byWaiter.StatusCode));
        var rows = await Read<List<ImportRow>>(preview);
        // Строки прошли ту же проверку, что и файл: продукт найден, цена за единицу посчитана, неизвестный товар — ошибка.
        Assert.Equal((product.Id, 2.5m, 36, 1200m, ImportRowStatus.Ok), (rows[0].ProductId!.Value, rows[0].Quantity!.Value, rows[0].ShelfLifeHours!.Value, rows[0].Cost!.Value, rows[0].Status));
        Assert.Equal(ImportRowStatus.Error, rows[1].Status);
        // Предпросмотр ничего не сохраняет: партии появятся только после подтверждения человеком.
        Assert.Empty(await Batches(product));

        // Распознавание платное: больше двадцати снимков в час ресторан не отправит.
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < PhotoQuota.PerHour; i++) codes.Add((await Api.PostAsync("/api/supplies/import/preview", Photo())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        fx.Time.Advance(TimeSpan.FromHours(1));

        fx.Invoices.Enabled = false;
        var off = await Api.PostAsync("/api/supplies/import/preview", Photo());
        fx.Invoices.Enabled = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, off.StatusCode);
    }

    /// <summary>Заметки и задачи из карточки заявки или ресторана.</summary>
    private sealed record CrmLists(List<CrmNoteResponse> NotesList, List<CrmTaskResponse> Tasks);

    /// <summary>Заявка с лендинга — такой, какой её видит CRM.</summary>
    private async Task<CrmLeadRow> CrmLead(HttpClient support)
    {
        var organization = $"Ресторан {Guid.NewGuid():N}";
        using var visitor = fx.CreateClient();
        (await visitor.PostAsJsonAsync("/api/leads", new LeadRequest("Мария", organization, "maria@example.com", "1", null))).EnsureSuccessStatusCode();
        return Assert.Single(await Get<List<CrmLeadRow>>(support, "/api/crm/leads?status=New"), l => l.Organization == organization);
    }

    private static async Task<Guid> RestaurantId(HttpClient owner) => (await Get<MeResponse>(owner, "/api/me")).RestaurantId;

    [Fact]
    public async Task Заявка_проходит_воронку_и_по_ней_открывается_кабинет()
    {
        using var support = await fx.SignInAsSupportAsync();
        var lead = await CrmLead(support);
        Assert.Equal((LeadStatus.New, lead.CreatedAt), (lead.Status, lead.UpdatedAt));
        Assert.Null(lead.Assignee);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        var moved = await Read<CrmLeadRow>(await support.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new CrmLeadUpdate(LeadStatus.Demo, " Аслан "), ApiFixture.Json));
        var noStatus = await support.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new CrmLeadUpdate(null, null), ApiFixture.Json);
        var unknown = await support.PutAsJsonAsync("/api/crm/leads/0", new CrmLeadUpdate(LeadStatus.Lost, null), ApiFixture.Json);

        Assert.Equal((LeadStatus.Demo, "Аслан"), (moved.Status, moved.Assignee));
        Assert.True(moved.UpdatedAt > lead.UpdatedAt);
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (noStatus.StatusCode, unknown.StatusCode));
        Assert.DoesNotContain(await Get<List<CrmLeadRow>>(support, "/api/crm/leads?status=New"), l => l.Id == lead.Id);
        Assert.Contains(await Get<List<CrmLeadRow>>(support, "/api/crm/leads?status=Demo"), l => l.Id == lead.Id);
        Assert.Contains(await Get<List<CrmLeadRow>>(support, "/api/crm/leads"), l => l.Id == lead.Id);
        Assert.True((await Get<CrmSummary>(support, "/api/crm/summary")).Leads.Demo >= 1);

        // Кабинет открывается прямо из заявки: она становится выигранной и запоминает свой ресторан.
        var login = $"c{Guid.NewGuid():N}"[..20];
        var opened = await support.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/restaurant", new RestaurantCreateRequest("Кафе Марии", login));
        var again = await support.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/restaurant", new RestaurantCreateRequest("Кафе Марии 2", $"d{login}"[..20]));
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (opened.StatusCode, again.StatusCode));
        var access = await Read<CrmAccess>(opened);
        Assert.Equal(("Кафе Марии", login), (access.Name, access.Login));

        using var owner = await fx.SignInAsync(access.Login, access.Password);
        var restaurant = await RestaurantId(owner);
        var won = await Get<CrmLeadRow>(support, $"/api/crm/leads/{lead.Id}");
        Assert.Equal((LeadStatus.Won, "Кафе Марии", restaurant), (won.Status, won.RestaurantName, won.RestaurantId));
        Assert.Equal(lead.Id, Assert.Single(await Get<List<CrmRestaurantRow>>(support, "/api/crm/restaurants"), r => r.Id == restaurant).LeadId);
        // Прежний список заявок, по которому работает бот, никуда не делся.
        Assert.Contains(await Get<List<LeadResponse>>(support, "/api/leads"), l => l.Id == lead.Id);
    }

    [Fact]
    public async Task CRM_закрыта_для_всех_кроме_команды_продукта()
    {
        using var anonymous = fx.CreateClient();
        using var manager = await fx.SignInAsync("manager");
        using var support = await fx.SignInAsSupportAsync();
        var (owner, _, _) = await fx.NewRestaurantAsync("Посторонние");
        var restaurant = await RestaurantId(owner);

        foreach (var url in new[] { "/api/crm/summary", "/api/crm/leads", "/api/crm/restaurants", $"/api/crm/restaurants/{restaurant}", "/api/crm/chats", "/api/crm/tasks" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            // Открытый управляющий демо, шеф-повар демо и управляющий другого ресторана — не команда продукта.
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Api.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await support.GetAsync(url)).StatusCode);
        }

        // Менять чужие рестораны они тоже не могут.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/crm/restaurants", new RestaurantCreateRequest("Филиал", "branch.two"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/crm/restaurants/{restaurant}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/crm/restaurants/{restaurant}/reset-password", new CrmResetRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/crm/restaurants/{restaurant}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/stop-list")).StatusCode);
    }

    [Fact]
    public async Task CRM_видит_активность_ресторана_но_не_его_склад()
    {
        var (owner, login, _) = await fx.NewRestaurantAsync("Счётчики");
        var restaurant = await RestaurantId(owner);
        var product = await Product(owner);
        var dish = await Read<DishResponse>(await owner.PostAsJsonAsync("/api/dishes", new DishRequest("Секретный суп", 400, [new RecipeItemDto(product.Id, 0.2m)])));
        await Supply(owner, new SupplyItemRequest(product.Id, 5, null));
        using var waiter = await Staff(owner, Role.Waiter);
        (await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)]))).EnsureSuccessStatusCode();
        using var support = await fx.SignInAsSupportAsync();

        var rows = await Get<List<CrmRestaurantRow>>(support, "/api/crm/restaurants");
        var row = Assert.Single(rows, r => r.Id == restaurant);

        Assert.Equal(("Счётчики", false, 2, login, 1, 1), (row.Name, row.IsDemo, row.Staff, row.ManagerLogin, row.Products, row.OrdersLast7Days));
        Assert.Equal(fx.Time.GetUtcNow(), row.LastActivityAt);
        Assert.Null(row.DisabledAt);
        Assert.Equal(Restaurant.DemoId, Assert.Single(rows, r => r.IsDemo).Id);
        var summary = await Get<CrmSummary>(support, "/api/crm/summary");
        Assert.True(summary.Restaurants >= 1 && summary.RestaurantsActive7d >= 1 && summary.RestaurantsActive7d <= summary.Restaurants);

        // Карточка называет сотрудников и счётчики — и больше ничего: ни продуктов, ни блюд, ни заказов построчно.
        var json = await support.GetStringAsync($"/api/crm/restaurants/{restaurant}");
        var card = System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.Equal(
            ["createdAt", "disabledAt", "id", "isDemo", "lastActivityAt", "leadId", "managerLogin", "name", "notes", "notesList", "openTasks",
             "ordersLast7Days", "products", "staff", "tasks", "users"],
            card.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["login", "role", "title"], card.GetProperty("users")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Contains(login, card.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("login").GetString()));
        Assert.DoesNotContain(product.Id.ToString(), json);
        Assert.Equal(HttpStatusCode.NotFound, (await support.GetAsync($"/api/crm/restaurants/{Guid.NewGuid()}")).StatusCode);

        // Новый ресторан заводится и из CRM; занятый логин не проходит.
        var created = await support.PostAsJsonAsync("/api/crm/restaurants", new RestaurantCreateRequest("Из CRM", $"n{login}"[..20]));
        var taken = await support.PostAsJsonAsync("/api/crm/restaurants", new RestaurantCreateRequest("Двойник", login));
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (created.StatusCode, taken.StatusCode));
        var access = await Read<CrmAccess>(created);
        using var fresh = await fx.SignInAsync(access.Login, access.Password);
        Assert.Equal("Из CRM", (await Get<MeResponse>(fresh, "/api/me")).Restaurant);
    }

    [Fact]
    public async Task Сброс_пароля_управляющего_отменяет_старый_пароль_и_старый_токен()
    {
        var (owner, login, password) = await fx.NewRestaurantAsync("Забывчивые");
        var restaurant = await RestaurantId(owner);
        using var support = await fx.SignInAsSupportAsync();

        var reset = await Read<CrmPassword>(await support.PostAsJsonAsync($"/api/crm/restaurants/{restaurant}/reset-password", new CrmResetRequest(null)));

        Assert.Equal(login, reset.Login);
        Assert.NotEqual(password, reset.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(login, password)).StatusCode);
        using var again = await fx.SignInAsync(login, reset.Password);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/me")).StatusCode);

        // Сотруднику пароль сбрасывается по логину; чужой или несуществующий логин не находится.
        var staffLogin = $"s{login}"[..20];
        (await again.PostAsJsonAsync("/api/users", new StaffRequest(staffLogin, "staff-2026-pw", "Повар", Role.Cook), ApiFixture.Json)).EnsureSuccessStatusCode();
        var staffReset = await Read<CrmPassword>(await support.PostAsJsonAsync($"/api/crm/restaurants/{restaurant}/reset-password", new CrmResetRequest(staffLogin)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(staffLogin, "staff-2026-pw")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(staffLogin, staffReset.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.PostAsJsonAsync($"/api/crm/restaurants/{restaurant}/reset-password", new CrmResetRequest("demo"))).StatusCode);

        // Открытые записи демо и сама поддержка защищены: их пароль CRM не меняет.
        foreach (var demo in new string?[] { null, "demo", "support" })
            Assert.Equal(HttpStatusCode.Conflict,
                (await support.PostAsJsonAsync($"/api/crm/restaurants/{Restaurant.DemoId}/reset-password", new CrmResetRequest(demo))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/api/crm/summary")).StatusCode);
    }

    [Fact]
    public async Task Отключённый_ресторан_не_входит_и_теряет_действующие_токены_пока_его_не_включат()
    {
        var (owner, login, password) = await fx.NewRestaurantAsync("Должники");
        var restaurant = await RestaurantId(owner);
        using var support = await fx.SignInAsSupportAsync();

        var disabled = await support.PostAsync($"/api/crm/restaurants/{restaurant}/disable", null);

        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/stop-list")).StatusCode);
        var refused = await Login(login, password);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("отключён", (await Read<System.Text.Json.JsonElement>(refused)).GetProperty("detail").GetString());
        Assert.Equal(fx.Time.GetUtcNow(), Assert.Single(await Get<List<CrmRestaurantRow>>(support, "/api/crm/restaurants"), r => r.Id == restaurant).DisabledAt);
        // Меню для гостей отключённого ресторана тоже закрыто.
        Assert.Equal(HttpStatusCode.NotFound, (await fx.CreateClient().GetAsync($"/api/public/menu/{restaurant}")).StatusCode);
        // Остальных отключение не касается.
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/api/stop-list")).StatusCode);

        var enabled = await support.PostAsync($"/api/crm/restaurants/{restaurant}/enable", null);

        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        // Токен, выданный до отключения, не оживает: сотрудник входит заново.
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/stop-list")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(login, password)).StatusCode);
        // Отключение и включение остаются заметками о ресторане.
        Assert.Equal(["Кабинет включён.", "Кабинет отключён."], (await Get<CrmLists>(support, $"/api/crm/restaurants/{restaurant}")).NotesList.Select(n => n.Text));
        Assert.Null(Assert.Single(await Get<List<CrmRestaurantRow>>(support, "/api/crm/restaurants"), r => r.Id == restaurant).DisabledAt);

        // Демо-ресторан — рабочее место самой команды продукта.
        Assert.Equal(HttpStatusCode.Conflict, (await support.PostAsync($"/api/crm/restaurants/{Restaurant.DemoId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.PostAsync($"/api/crm/restaurants/{Guid.NewGuid()}/disable", null)).StatusCode);
    }

    [Fact]
    public async Task Заметки_и_задачи_ведутся_по_заявке_и_по_ресторану()
    {
        using var support = await fx.SignInAsSupportAsync();
        var lead = await CrmLead(support);
        var (owner, _, _) = await fx.NewRestaurantAsync("С заметками");
        var restaurant = await RestaurantId(owner);
        Task<HttpResponseMessage> Note(long? leadId, Guid? restaurantId, string text) =>
            support.PostAsJsonAsync("/api/crm/notes", new CrmNoteRequest(leadId, restaurantId, text));

        var noted = await Note(lead.Id, null, "Перезвонить после шести");
        var aboutRestaurant = await Note(null, restaurant, "Просили счёт на год");
        HttpResponseMessage[] rejected =
            [await Note(lead.Id, restaurant, "И туда, и сюда"), await Note(null, null, "Ни о чём"), await Note(lead.Id, null, " "), await Note(0, null, "Нет такой заявки")];

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (noted.StatusCode, aboutRestaurant.StatusCode));
        Assert.All(rejected, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        var note = await Read<CrmNoteResponse>(noted);
        Assert.Equal(("support", "Перезвонить после шести", lead.Id), (note.Author, note.Text, note.LeadId));

        // Задачи: одна просрочена, вторая — о ресторане, на потом.
        var now = fx.Time.GetUtcNow();
        var before = await Get<CrmSummary>(support, "/api/crm/summary");
        var late = await Read<CrmTaskResponse>(await support.PostAsJsonAsync("/api/crm/tasks", new CrmTaskRequest("Отправить договор", now.AddHours(-1), lead.Id, null)));
        // Срок приходит с часовым поясом того, кто его назначил.
        var later = await Read<CrmTaskResponse>(await support.PostAsJsonAsync("/api/crm/tasks",
            new CrmTaskRequest("Спросить, как идёт запуск", now.AddDays(30).ToOffset(TimeSpan.FromHours(3)), null, restaurant)));
        var noTitle = await support.PostAsJsonAsync("/api/crm/tasks", new CrmTaskRequest("", now, null, null));
        var noDue = await support.PostAsJsonAsync("/api/crm/tasks", new CrmTaskRequest("Без срока", null, null, null));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (noTitle.StatusCode, noDue.StatusCode));
        Assert.Equal(("Мария", null, "С заметками", now.AddDays(30)), (late.LeadName, late.RestaurantName, later.RestaurantName, later.DueAt));
        Assert.Equal(before.TasksOverdue + 1, (await Get<CrmSummary>(support, "/api/crm/summary")).TasksOverdue);

        var open = await Get<List<CrmTaskResponse>>(support, "/api/crm/tasks?open=true");
        Assert.True(open.FindIndex(t => t.Id == late.Id) < open.FindIndex(t => t.Id == later.Id));
        Assert.Equal(open.OrderBy(t => t.DueAt).Select(t => t.Id), open.Select(t => t.Id));
        var card = await Get<CrmLists>(support, $"/api/crm/leads/{lead.Id}");
        Assert.Equal((note.Id, late.Id), (Assert.Single(card.NotesList).Id, Assert.Single(card.Tasks).Id));
        var row = await Get<CrmLeadRow>(support, $"/api/crm/leads/{lead.Id}");
        Assert.Equal((1, 1), (row.Notes, row.OpenTasks));
        var restaurantCard = await Get<CrmLists>(support, $"/api/crm/restaurants/{restaurant}");
        Assert.Equal(("Просили счёт на год", later.Id), (Assert.Single(restaurantCard.NotesList).Text, Assert.Single(restaurantCard.Tasks).Id));

        // Сделанная задача уходит из открытых, но остаётся в общем списке; отметку можно снять.
        var done = await Read<CrmTaskResponse>(await support.PutAsJsonAsync($"/api/crm/tasks/{late.Id}", new CrmTaskUpdate("Отправить договор и счёт", late.DueAt, true)));
        Assert.Equal(("Отправить договор и счёт", now), (done.Title, done.DoneAt));
        Assert.DoesNotContain(await Get<List<CrmTaskResponse>>(support, "/api/crm/tasks?open=true"), t => t.Id == late.Id);
        Assert.Contains(await Get<List<CrmTaskResponse>>(support, "/api/crm/tasks"), t => t.Id == late.Id);
        Assert.Equal(0, (await Get<CrmLeadRow>(support, $"/api/crm/leads/{lead.Id}")).OpenTasks);
        var reopened = await Read<CrmTaskResponse>(await support.PutAsJsonAsync($"/api/crm/tasks/{late.Id}", new CrmTaskUpdate(done.Title, late.DueAt, false)));
        Assert.Null(reopened.DoneAt);

        Assert.Equal(HttpStatusCode.NoContent, (await support.DeleteAsync($"/api/crm/tasks/{late.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.DeleteAsync($"/api/crm/tasks/{late.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await support.DeleteAsync($"/api/crm/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.DeleteAsync($"/api/crm/notes/{note.Id}")).StatusCode);
        Assert.Empty((await Get<CrmLists>(support, $"/api/crm/leads/{lead.Id}")).NotesList);

        // Текст заметки — личные данные: в базе он лежит зашифрованным.
        await using var scope = fx.DemoScope();
        var raw = await scope.ServiceProvider.GetRequiredService<Ostatok.Api.Data.AppDbContext>().Database
            .SqlQuery<string>($"""SELECT "Text" AS "Value" FROM "CrmNotes" """).ToListAsync();
        Assert.NotEmpty(raw);
        Assert.All(raw, value => Assert.StartsWith("v1:", value));
    }

    [Fact]
    public async Task Диалог_привязывается_к_заявке_и_заявка_заводится_из_диалога()
    {
        using var visitor = fx.CreateClient();
        using var support = await fx.SignInAsSupportAsync();
        var name = $"Гость {Guid.NewGuid():N}";
        var chat = await Read<ChatResponse>(await visitor.PostAsJsonAsync("/api/chats", new ChatStartRequest(name, "+7 900 111-22-33", "Сколько стоит на три точки?")));
        await visitor.PostAsJsonAsync($"/api/chats/{chat.Id}/messages", new ChatTextRequest("И есть ли пробный период?"));
        async Task<CrmChatRow> Row() => Assert.Single(await Get<List<CrmChatRow>>(support, "/api/crm/chats"), c => c.Id == chat.Id);

        var row = await Row();
        Assert.Equal((2, "И есть ли пробный период?", null), (row.Unread, row.LastText, row.LeadId));

        // Заявка из диалога: имя и контакт посетителя, комментарий — его первый вопрос.
        var created = await support.PostAsync($"/api/crm/chats/{chat.Id}/lead", null);
        var again = await support.PostAsync($"/api/crm/chats/{chat.Id}/lead", null);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (created.StatusCode, again.StatusCode));
        var lead = await Read<CrmLeadRow>(created);
        Assert.Equal((name, "+7 900 111-22-33", "—", "Сколько стоит на три точки?", LeadStatus.New, chat.Id),
            (lead.Name, lead.Contact, lead.Organization, lead.Comment, lead.Status, lead.ChatId));
        Assert.Equal(lead.Id, (await Row()).LeadId);

        // Диалог можно отвязать и привязать к другой заявке — той, что пришла с формы.
        var other = await CrmLead(support);
        Task<HttpResponseMessage> Link(Guid chatId, long? leadId) => support.PutAsJsonAsync($"/api/crm/chats/{chatId}/lead", new CrmChatLead(leadId));
        Assert.Equal(HttpStatusCode.NoContent, (await Link(chat.Id, null)).StatusCode);
        Assert.Null((await Row()).LeadId);
        Assert.Null((await Get<CrmLeadRow>(support, $"/api/crm/leads/{lead.Id}")).ChatId);
        Assert.Equal(HttpStatusCode.NoContent, (await Link(chat.Id, other.Id)).StatusCode);
        Assert.Equal(chat.Id, (await Get<CrmLeadRow>(support, $"/api/crm/leads/{other.Id}")).ChatId);
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), ((await Link(chat.Id, 0)).StatusCode, (await Link(Guid.NewGuid(), other.Id)).StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, (await support.PostAsync($"/api/crm/chats/{Guid.NewGuid()}/lead", null)).StatusCode);

        // Переписка и ответ остались на прежних адресах.
        Assert.Equal(2, (await Get<ChatDetails>(support, $"/api/support/chats/{chat.Id}")).Messages.Count);
        Assert.Equal(HttpStatusCode.Created, (await support.PostAsJsonAsync($"/api/support/chats/{chat.Id}/messages", new ChatTextRequest("Да, две недели."))).StatusCode);
        Assert.Equal(0, (await Row()).Unread);
    }

    [Fact]
    public async Task Владелец_добавляет_себе_ресторан_и_переходит_в_него_а_данные_не_смешиваются()
    {
        var (owner, _, _) = await fx.NewRestaurantAsync("Первая точка");
        var (stranger, _, _) = await fx.NewRestaurantAsync("Чужой ресторан");
        using var demoManager = await fx.SignInAsync("manager");
        Task<HttpResponseMessage> Product(HttpClient client, string name) =>
            client.PostAsJsonAsync("/api/products", new ProductRequest(name, Unit.Kg, 48, 100), ApiFixture.Json);
        (await Product(owner, "Только в первой")).EnsureSuccessStatusCode();

        var added = await owner.PostAsJsonAsync("/api/my/restaurants", new MyRestaurantRequest("Вторая точка"));
        var fromDemo = await demoManager.PostAsJsonAsync("/api/my/restaurants", new MyRestaurantRequest("Самозванец"));
        var byWaiter = await fx.Waiter.PostAsJsonAsync("/api/my/restaurants", new MyRestaurantRequest("Самозванец"));

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (added.StatusCode, fromDemo.StatusCode, byWaiter.StatusCode));
        var second = await Read<MyRestaurant>(added);
        var mine = (await owner.GetFromJsonAsync<List<MyRestaurant>>("/api/my/restaurants", ApiFixture.Json))!;
        Assert.Equal([("Первая точка", true), ("Вторая точка", false)], mine.Select(r => (r.Name, r.Current)));
        // Чужой управляющий в этот ресторан не попадёт, даже зная его идентификатор.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/my/restaurants/{second.Id}/enter", null)).StatusCode);

        var entered = await owner.PostAsync($"/api/my/restaurants/{second.Id}/enter", null);
        using var inSecond = fx.CreateClient();
        inSecond.DefaultRequestHeaders.Authorization = new("Bearer", (await Read<LoginResponse>(entered)).Token);

        // Во втором ресторане пусто: продукты первого сюда не попали, а заведённое здесь не видно в первом.
        var me = (await inSecond.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json))!;
        Assert.Equal(("Вторая точка", second.Id), (me.Restaurant, me.RestaurantId));
        Assert.Empty((await inSecond.GetFromJsonAsync<List<ProductResponse>>("/api/products", ApiFixture.Json))!);
        (await Product(inSecond, "Только во второй")).EnsureSuccessStatusCode();
        Assert.Equal(["Только в первой"], (await owner.GetFromJsonAsync<List<ProductResponse>>("/api/products", ApiFixture.Json))!.Select(p => p.Name));
        Assert.Equal(["Только во второй"], (await inSecond.GetFromJsonAsync<List<ProductResponse>>("/api/products", ApiFixture.Json))!.Select(p => p.Name));
        // Во втором ресторане владелец заводит своих сотрудников, сигналы у него работают.
        Assert.Equal(HttpStatusCode.OK, (await inSecond.GetAsync("/api/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await inSecond.PostAsJsonAsync("/api/users",
            new StaffRequest($"w{Guid.NewGuid():N}"[..20], "password-2026", "Официант", Role.Waiter), ApiFixture.Json)).StatusCode);

        // Команда продукта отключила второй ресторан: токен владельца для него перестал работать, первый ресторан жив.
        using var support = await fx.SignInAsSupportAsync();
        (await support.PostAsync($"/api/crm/restaurants/{second.Id}/disable", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await inSecond.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/my/restaurants/{second.Id}/enter", null)).StatusCode);
    }
}
