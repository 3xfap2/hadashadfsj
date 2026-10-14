using System.Net;
using System.Net.Http.Json;
using Ostatok.Api.Domain;
using Ostatok.Api.Features;

namespace Ostatok.Tests;

/// <summary>Кабинет владельца: сводка, сотрудники, поставщики и переписка по всем его ресторанам — и только по ним.</summary>
public class OwnerTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiFixture.Json))!;
    }

    private static async Task<T> Get<T>(HttpClient client, string url) => (await client.GetFromJsonAsync<T>(url, ApiFixture.Json))!;

    /// <summary>Владелец двух ресторанов: первый открыла поддержка, второй он добавил себе сам.</summary>
    private async Task<(HttpClient Owner, string Login, Guid Home, Guid Second)> OwnerAsync()
    {
        var (owner, login, _) = await fx.NewRestaurantAsync($"Первая {Guid.NewGuid():N}"[..20]);
        var home = (await Get<MeResponse>(owner, "/api/me")).RestaurantId;
        var second = await Read<MyRestaurant>(await owner.PostAsJsonAsync("/api/my/restaurants", new MyRestaurantRequest("Вторая точка")));
        return (owner, login, home, second.Id);
    }

    /// <summary>Заводит сотрудника в ресторане владельца через кабинет владельца и входит под ним.</summary>
    private async Task<(HttpClient Client, string Login)> EmployeeAsync(HttpClient owner, Guid restaurant, Role role = Role.Waiter)
    {
        var login = $"e{Guid.NewGuid():N}"[..20];
        var created = await owner.PostAsJsonAsync($"/api/owner/restaurants/{restaurant}/users", new StaffRequest(login, "employee-pass-2026", "Сотрудник", role), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await fx.SignInAsync(login, "employee-pass-2026"), login);
    }

    /// <summary>Клиент владельца, перешедшего в другой свой ресторан.</summary>
    private async Task<HttpClient> EnterAsync(HttpClient owner, Guid restaurant)
    {
        var token = await Read<LoginResponse>(await owner.PostAsync($"/api/my/restaurants/{restaurant}/enter", null));
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        return client;
    }

    private static async Task<ProductResponse> ProductAsync(HttpClient client, string name, decimal cost, int shelfLifeHours = 48) =>
        await Read<ProductResponse>(await client.PostAsJsonAsync("/api/products", new ProductRequest(name, Unit.Kg, shelfLifeHours, cost), ApiFixture.Json));

    private static async Task<SupplyResponse> SupplyAsync(HttpClient client, ProductResponse product, decimal quantity, string? supplier = null, int? shelfLifeHours = null) =>
        await Read<SupplyResponse>(await client.PostAsJsonAsync("/api/supplies",
            new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, quantity, shelfLifeHours)], supplier)));

    private static async Task WriteOffAsync(HttpClient client, SupplyResponse supply, WriteOffCause reason) =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/stock/batches/{supply.Items.Single().BatchId}/write-off", new WriteOffRequest(reason), ApiFixture.Json)).StatusCode);

    [Fact]
    public async Task Кабинет_владельца_открыт_только_владельцу()
    {
        var (owner, _, _, _) = await OwnerAsync();
        var (manager, _, _) = await fx.NewRestaurantAsync("Без своих ресторанов");
        using var demoManager = await fx.SignInAsync("manager");

        foreach (var url in new[] { "/api/owner/summary", "/api/owner/staff", "/api/owner/suppliers", "/api/owner/messages" })
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(url)).StatusCode);
            // Управляющий, который не добавлял себе ресторанов, — не владелец: понятный отказ, а не пустые данные.
            var refused = await manager.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("только владельцу", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await demoManager.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fx.Waiter.GetAsync(url)).StatusCode);
        }
        // По разделам сайт показывает владельцу ссылку на кабинет владельца, остальным — нет.
        Assert.Contains("owner", (await Get<MeResponse>(owner, "/api/me")).Sections);
        Assert.DoesNotContain("owner", (await Get<MeResponse>(manager, "/api/me")).Sections);
    }

    [Fact]
    public async Task Сводка_складывает_выручку_списания_и_партии_по_всем_ресторанам_владельца()
    {
        var (owner, _, home, second) = await OwnerAsync();
        using var inSecond = await EnterAsync(owner, second);
        var (waiter, _) = await EmployeeAsync(owner, home);
        // Чужой ресторан в сводку не попадает.
        var (stranger, _, _) = await fx.NewRestaurantAsync("Чужой");
        var strangerProduct = await ProductAsync(stranger, "Чужая говядина", 900);
        await WriteOffAsync(stranger, await SupplyAsync(stranger, strangerProduct, 5), WriteOffCause.Defective);

        // Первый ресторан: заказ на 2 порции по 500 ₽, остаток молока списан как брак.
        var milk = await ProductAsync(owner, "Молоко", 100);
        var dish = await Read<DishResponse>(await owner.PostAsJsonAsync("/api/dishes", new DishRequest("Каша", 500, [new RecipeItemDto(milk.Id, 1)])));
        var milkSupply = await SupplyAsync(owner, milk, 10);
        Assert.Equal(HttpStatusCode.Created, (await waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 2)]))).StatusCode);
        await WriteOffAsync(owner, milkSupply, WriteOffCause.Defective);
        // Второй ресторан: молоко (тот же продукт по названию) испортилось, сыр истекает в ближайшие сутки.
        var milk2 = await ProductAsync(inSecond, "Молоко", 50);
        await WriteOffAsync(inSecond, await SupplyAsync(inSecond, milk2, 4), WriteOffCause.Spoiled);
        var cheese = await ProductAsync(inSecond, "Сыр", 300);
        await SupplyAsync(inSecond, cheese, 1, shelfLifeHours: 10);

        var summary = await Get<OwnerSummary>(owner, "/api/owner/summary?days=1");

        var (first, other) = (summary.Restaurants.Single(r => r.Id == home), summary.Restaurants.Single(r => r.Id == second));
        Assert.Equal(2, summary.Restaurants.Count());
        Assert.Equal((1000m, 1, 800m, 0m, 1), (first.Revenue, first.Orders, first.WriteOffs.Defective, first.WriteOffs.Spoiled, first.Stopped));
        Assert.Equal((0m, 0, 200m, 0m, 1, 0), (other.Revenue, other.Orders, other.WriteOffs.Spoiled, other.WriteOffs.Defective, other.ExpiringSoon, other.ToWriteOff));
        Assert.Equal((1000m, 1, 1000m, 1), (summary.Total.Revenue, summary.Total.Orders, summary.Total.WriteOffs.Total, summary.Total.ExpiringSoon));
        // Молоко списывали в обоих ресторанах: в топе оно одно, на 1000 ₽, два раза, в двух ресторанах.
        var top = Assert.Single(summary.TopWrittenOff);
        Assert.Equal(("Молоко", 2, 12m, 1000m, 2), (top.ProductName, top.Times, top.Quantity, top.Value, top.Restaurants));

        // Через двое суток сыр просрочен и ждёт списания, а списания «за сегодня» уже не считаются.
        fx.Time.Advance(TimeSpan.FromDays(2));
        var later = await Get<OwnerSummary>(owner, "/api/owner/summary?days=1");
        Assert.Equal((1, 0, 0m), (later.Total.ToWriteOff, later.Total.ExpiringSoon, later.Total.WriteOffs.Total));
        Assert.Equal(1000m, (await Get<OwnerSummary>(owner, "/api/owner/summary?days=7")).Total.WriteOffs.Total);
    }

    [Fact]
    public async Task Владелец_управляет_сотрудниками_своих_ресторанов_но_не_чужих()
    {
        var (owner, ownerLogin, home, second) = await OwnerAsync();
        var (stranger, _, _) = await fx.NewRestaurantAsync("Чужой");
        var strangerHome = (await Get<MeResponse>(stranger, "/api/me")).RestaurantId;
        var strangerUser = (await Get<List<StaffResponse>>(stranger, "/api/users")).Single();
        var login = $"c{Guid.NewGuid():N}"[..20];

        var created = await owner.PostAsJsonAsync($"/api/owner/restaurants/{second}/users", new StaffRequest(login, "cook-pass-2026", "Повар", Role.Cook), ApiFixture.Json);
        var weak = await owner.PostAsJsonAsync($"/api/owner/restaurants/{second}/users", new StaffRequest($"w{Guid.NewGuid():N}"[..20], "12345678", "Повар", Role.Cook), ApiFixture.Json);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.BadRequest), (created.StatusCode, weak.StatusCode));
        var cook = await Read<StaffResponse>(created);
        var staff = await Get<List<OwnerStaffRow>>(owner, "/api/owner/staff");
        Assert.Equal([(home, ownerLogin, true), (second, login, false)], staff.Select(s => (s.RestaurantId, s.Login, s.Owner)));
        // Сотрудник заведён именно во втором ресторане: там он виден, в первом — нет.
        using var inSecond = await EnterAsync(owner, second);
        Assert.Contains(login, (await Get<List<StaffResponse>>(inSecond, "/api/users")).Select(u => u.Login));
        Assert.DoesNotContain(login, (await Get<List<StaffResponse>>(owner, "/api/users")).Select(u => u.Login));

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/owner/restaurants/{second}/users/{cook.Id}", new StaffUpdate("Шеф", Role.Chef, "chef-pass-2026"), ApiFixture.Json)).StatusCode);
        Assert.Equal(Role.Chef, (await Get<List<OwnerStaffRow>>(owner, "/api/owner/staff")).Single(s => s.Id == cook.Id).Role);
        // Те же проверки, что в кабинете ресторана: свою роль не снять.
        var self = staff.Single(s => s.Owner);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/owner/restaurants/{home}/users/{self.Id}", new StaffUpdate("Владелец", Role.Waiter), ApiFixture.Json)).StatusCode);
        // Сотрудник ищется только в выбранном ресторане: в первом ресторане повара второго нет.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/owner/restaurants/{home}/users/{cook.Id}")).StatusCode);

        // Чужой ресторан — как будто его нет, даже зная идентификаторы.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/owner/restaurants/{strangerHome}/users", new StaffRequest($"x{Guid.NewGuid():N}"[..20], "stranger-pass-2026", "Повар", Role.Cook), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/owner/restaurants/{strangerHome}/users/{strangerUser.Id}", new StaffUpdate("Официант", Role.Waiter, "taken-over-2026"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/owner/restaurants/{strangerHome}/users/{strangerUser.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/owner/restaurants/{second}/users/{strangerUser.Id}")).StatusCode);
        Assert.Equal(Role.Manager, (await Get<List<StaffResponse>>(stranger, "/api/users")).Single().Role);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/owner/restaurants/{second}/users/{cook.Id}")).StatusCode);
        Assert.DoesNotContain(cook.Id, (await Get<List<OwnerStaffRow>>(owner, "/api/owner/staff")).Select(s => s.Id));
    }

    [Fact]
    public async Task Рейтинг_поставщиков_сводит_все_рестораны_и_ставит_выше_того_у_кого_меньше_брака()
    {
        var (owner, _, _, second) = await OwnerAsync();
        using var inSecond = await EnterAsync(owner, second);
        var (stranger, _, _) = await fx.NewRestaurantAsync("Чужой");
        var tag = Guid.NewGuid().ToString("N")[..6];
        var (good, bad) = ($"Ферма {tag}", $"Опт {tag}");

        // «Ферма» возит в оба ресторана без потерь; у «Опта» четверть поставленного — брак.
        await SupplyAsync(owner, await ProductAsync(owner, "Творог", 200), 5, good);
        var potatoes = await ProductAsync(inSecond, "Картофель", 40);
        await SupplyAsync(inSecond, potatoes, 10, good);
        await SupplyAsync(inSecond, potatoes, 30, bad);
        await WriteOffAsync(inSecond, await SupplyAsync(inSecond, potatoes, 10, bad), WriteOffCause.Defective);
        // Те же поставщики у чужого ресторана не влияют на рейтинг владельца.
        await WriteOffAsync(stranger, await SupplyAsync(stranger, await ProductAsync(stranger, "Творог", 200), 50, good), WriteOffCause.Defective);

        var rating = await Get<OwnerSuppliers>(owner, "/api/owner/suppliers");

        Assert.Equal([good, bad], rating.Suppliers.Select(s => s.Supplier));
        var (first, last) = (rating.Suppliers.First(), rating.Suppliers.Last());
        Assert.Equal((1, 2, 2, 1400m, 0m, 100), (first.Place, first.Restaurants, first.Supplies, first.Delivered, first.DefectivePercent, first.Score));
        Assert.Equal((2, 1, 1600m, 25m, 25), (last.Place, last.Restaurants, last.Delivered, last.DefectivePercent, last.Score));
        Assert.Equal(("Картофель", 40m, 40m), (last.Products.Single().Name, last.Products.Single().Quantity, last.Products.Single().AveragePrice));
        Assert.Equal(["Творог", "Картофель"], first.Products.Select(p => p.Name));
        Assert.Contains("брак", rating.Formula);
    }

    [Fact]
    public async Task Сотрудник_видит_только_свой_диалог_и_сообщения_всем_своего_ресторана()
    {
        var (owner, ownerLogin, home, second) = await OwnerAsync();
        var (alice, aliceLogin) = await EmployeeAsync(owner, home);
        var (bob, _) = await EmployeeAsync(owner, home, Role.Cook);
        var (carol, carolLogin) = await EmployeeAsync(owner, second);
        // У управляющего чужого ресторана владельца нет — и переписки тоже.
        var (stranger, _, _) = await fx.NewRestaurantAsync("Чужой");
        var strangerHome = (await Get<MeResponse>(stranger, "/api/me")).RestaurantId;
        Task<HttpResponseMessage> Send(Guid restaurant, string? employee, string text) =>
            owner.PostAsJsonAsync("/api/owner/messages", new OwnerMessageRequest(restaurant, employee, text));

        Assert.Equal(HttpStatusCode.Created, (await Send(home, aliceLogin, "Алиса, проверь молоко")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(home, null, "Всем: завтра инвентаризация")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(second, carolLogin, "Кэрол, привет")).StatusCode);
        // Чужой ресторан — 404, сотрудник из другого ресторана и сам владелец — ошибка адресата.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(strangerHome, null, "Это не мой ресторан")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(home, carolLogin, "Не тот ресторан")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(home, ownerLogin, "Себе")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/owner/messages/{strangerHome}")).StatusCode);

        var forAlice = await Get<MyMessages>(alice, "/api/messages");
        Assert.Equal(2, forAlice.Unread);
        Assert.Equal([("Алиса, проверь молоко", false), ("Всем: завтра инвентаризация", true)], forAlice.Messages.Select(m => (m.Text, m.ToAll)));
        Assert.Equal(["Всем: завтра инвентаризация"], (await Get<MyMessages>(bob, "/api/messages")).Messages.Select(m => m.Text));
        Assert.Equal(["Кэрол, привет"], (await Get<MyMessages>(carol, "/api/messages")).Messages.Select(m => m.Text));
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/messages")).StatusCode);
        Assert.Contains("messages", (await Get<MeResponse>(alice, "/api/me")).Sections);
        Assert.DoesNotContain("messages", (await Get<MeResponse>(stranger, "/api/me")).Sections);

        // Алиса прочитала и ответила: у неё ничего нового, у владельца — один непрочитанный ответ в её диалоге.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync("/api/messages/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/messages", new MessageTextRequest("Проверила, всё в порядке"))).StatusCode);
        Assert.Equal(0, (await Get<UnreadCount>(alice, "/api/messages/unread")).Unread);
        Assert.Equal(1, (await Get<UnreadCount>(bob, "/api/messages/unread")).Unread);
        // Ответ Алисы видит только она и владелец: у Боба его нет.
        Assert.DoesNotContain("Проверила, всё в порядке", (await Get<MyMessages>(bob, "/api/messages")).Messages.Select(m => m.Text));

        var dialogs = await Get<List<OwnerDialog>>(owner, "/api/owner/messages");
        Assert.Equal(3, dialogs.Count);
        var withAlice = dialogs.Single(d => d.RestaurantId == home && d.Employee == aliceLogin);
        Assert.Equal(("Проверила, всё в порядке", false, 1), (withAlice.LastText, withAlice.LastFromOwner, withAlice.Unread));
        var thread = await Get<List<StaffMessageRow>>(owner, $"/api/owner/messages/{home}?employee={aliceLogin}");
        Assert.Equal(["Алиса, проверь молоко", "Проверила, всё в порядке"], thread.Select(m => m.Text));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/owner/messages/{home}/read?employee={aliceLogin}", null)).StatusCode);
        Assert.Equal(0, (await Get<List<OwnerDialog>>(owner, "/api/owner/messages")).Single(d => d.Employee == aliceLogin).Unread);
        // Сам владелец переписывается из своего кабинета, а не как сотрудник.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/messages")).StatusCode);
    }
}
