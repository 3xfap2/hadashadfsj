using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Ostatok.Api.Domain;
using Ostatok.Api.Features;

namespace Ostatok.Tests;

/// <summary>Инфраструктура: проверки работоспособности, номер запроса, схемы доступа в описании API, выключатель кэша и гонка заказа со списанием.</summary>
public class PlatformTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private HttpClient Api => fx.Api;

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ApiFixture.Json))!;

    private sealed record Health(string Status, string Version, DateTimeOffset StartedAt, CacheStats Cache);
    private sealed record Ready(string Status, string Version);

    [Fact]
    public async Task Health_отдаёт_версию_и_время_запуска_а_ready_проверяет_базу()
    {
        var health = await Read<Health>(await fx.CreateClient().GetAsync("/health"));
        var ready = await fx.CreateClient().GetAsync("/health/ready");

        Assert.Equal(("ok", "local"), (health.Status, health.Version));
        Assert.True(health.StartedAt <= fx.Time.GetUtcNow());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(new Ready("ready", "local"), await Read<Ready>(ready));
    }

    [Fact]
    public void Версия_берётся_из_коммита_Render_затем_из_сборки()
    {
        static string Version(params (string Key, string Value)[] values) => Observability.Info(
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build(),
            new FakeTimeProvider()).Version;

        Assert.Equal("local", Version());
        Assert.Equal("abc123", Version(("GIT_SHA", "abc123")));
        Assert.Equal("def456", Version(("GIT_SHA", "abc123"), ("RENDER_GIT_COMMIT", "def456")));
        Assert.Equal("abc123", Version(("GIT_SHA", "abc123"), ("RENDER_GIT_COMMIT", "")));
    }

    [Fact]
    public async Task Номер_запроса_возвращается_в_ответе_свой_или_новый()
    {
        using var client = fx.CreateClient();
        using var given = new HttpRequestMessage(HttpMethod.Get, "/api/stock");
        given.Headers.Add(Observability.RequestIdHeader, "lb-7f3a.42");
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/health");
        forged.Headers.TryAddWithoutValidation(Observability.RequestIdHeader, "<script>alert(1)</script>");

        var withId = await client.SendAsync(given);
        var withoutId = await client.GetAsync("/health");
        var withForged = await client.SendAsync(forged);

        // Номер есть и в ответе с ошибкой (401 без токена): по нему ищется запрос в журнале.
        Assert.Equal(HttpStatusCode.Unauthorized, withId.StatusCode);
        Assert.Equal("lb-7f3a.42", withId.Headers.GetValues(Observability.RequestIdHeader).Single());
        Assert.Matches("^[0-9a-f]{32}$", withoutId.Headers.GetValues(Observability.RequestIdHeader).Single());
        Assert.Matches("^[0-9a-f]{32}$", withForged.Headers.GetValues(Observability.RequestIdHeader).Single());
    }

    [Fact]
    public async Task Описание_API_называет_ключ_интеграции_и_описывает_заказ()
    {
        using var client = fx.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var root = doc.RootElement;
        var apiKey = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal(("apiKey", "header", "X-Api-Key"), (apiKey.GetProperty("type").GetString(), apiKey.GetProperty("in").GetString(), apiKey.GetProperty("name").GetString()));
        var paths = root.GetProperty("paths");
        Assert.Equal("Оформить заказ (официант)", paths.GetProperty("/api/orders").GetProperty("post").GetProperty("summary").GetString());
        // Поставку извне принимают по токену или по ключу — два требования, «любое из».
        var incoming = paths.GetProperty("/api/supplies/incoming").GetProperty("post");
        Assert.Equal(["Bearer", "ApiKey"], incoming.GetProperty("security").EnumerateArray().Select(r => r.EnumerateObject().Single().Name));
        Assert.Equal("Приёмка поставки", paths.GetProperty("/api/supplies/incoming/{id}/accept").GetProperty("post").GetProperty("summary").GetString());
        // Вход открыт: требования токена у него нет.
        Assert.False(paths.GetProperty("/api/auth/login").GetProperty("post").TryGetProperty("security", out _));
    }

    [Fact]
    public async Task С_выключенным_кэшем_стоп_лист_каждый_раз_считается_по_базе()
    {
        await using var noCache = fx.WithWebHostBuilder(b => b.UseSetting("Cache:Enabled", "false"));
        using var client = noCache.CreateClient();
        client.DefaultRequestHeaders.Authorization = Api.DefaultRequestHeaders.Authorization;

        var first = await client.GetAsync("/api/stop-list");
        var second = await client.GetAsync("/api/stop-list");

        Assert.All([first, second], r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All([first, second], r => Assert.Equal("off", r.Headers.GetValues("X-Cache").Single()));
    }

    [Fact]
    public async Task Заказ_одновременно_с_ручным_списанием_той_же_партии_не_уводит_её_в_минус()
    {
        for (var round = 0; round < 5; round++)
        {
            var product = await Read<ProductResponse>(await Api.PostAsJsonAsync("/api/products",
                new ProductRequest($"Продукт {Guid.NewGuid():N}", Unit.Kg, 48, 1000), ApiFixture.Json));
            var dish = await Read<DishResponse>(await Api.PostAsJsonAsync("/api/dishes",
                new DishRequest($"Блюдо {Guid.NewGuid():N}", 500, [new RecipeItemDto(product.Id, 0.2m)])));
            var supply = await Read<SupplyResponse>(await Api.PostAsJsonAsync("/api/supplies",
                new SupplyRequest(null, $"П-{Guid.NewGuid():N}", null, [new SupplyItemRequest(product.Id, 1, null)])));
            var batchId = supply.Items.Single().BatchId;

            // Партия на 5 порций: 8 заказов и списание всей партии — одновременно; списание стоит в середине очереди.
            var orders = Enumerable.Range(0, 8).Select(_ => fx.Waiter.PostAsJsonAsync("/api/orders", new OrderRequest([new OrderLine(dish.Id, 1)]))).ToList();
            orders.Insert(round % 8, Api.PostAsJsonAsync($"/api/stock/batches/{batchId}/write-off", new WriteOffRequest(WriteOffCause.Spoiled), ApiFixture.Json));
            var responses = await Task.WhenAll(orders);
            var writeOff = responses[round % 8];
            var placed = responses.Where(r => r != writeOff).ToList();

            Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, $"{r.StatusCode}: {r.Content.ReadAsStringAsync().Result}"));
            Assert.Contains(writeOff.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
            Assert.All(placed, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
            var sold = placed.Count(r => r.StatusCode == HttpStatusCode.Created);
            Assert.InRange(sold, 0, 5);

            // Итог согласован: продано + списано = поставлено, партия пуста и не ушла в минус.
            var movements = (await Api.GetFromJsonAsync<List<MovementRow>>($"/api/movements?take=500&batchId={batchId}", ApiFixture.Json))!;
            var orderQuantity = movements.Where(m => m.Type == MovementType.Order).Sum(m => m.Quantity);
            var writtenOff = movements.Where(m => m.Type == MovementType.WriteOff).Sum(m => m.Quantity);
            Assert.Equal(sold * 0.2m, orderQuantity);
            Assert.Equal(1m, orderQuantity + writtenOff);
            Assert.Equal(writeOff.StatusCode == HttpStatusCode.NoContent, writtenOff > 0);
            var left = (await Api.GetFromJsonAsync<List<BatchRow>>($"/api/stock/batches?productId={product.Id}", ApiFixture.Json))!;
            Assert.Empty(left);
        }
    }
}
