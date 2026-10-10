using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Ostatok.Api.Features;
using Testcontainers.PostgreSql;

namespace Ostatok.Tests;

/// <summary>Поднимает API на настоящем PostgreSQL в контейнере и с управляемыми часами.</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    public HttpClient Api { get; private set; } = null!;
    /// <summary>Клиент официанта: заказы оформляет только он.</summary>
    public HttpClient Waiter { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Db", _postgres.GetConnectionString());
        builder.UseSetting("Auth:Key", "integration-tests-signing-key-0123456789");
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Auth:SupportPassword", SupportPassword);
        // Сценарии переводят часы вперёд на сутки и больше — токен не должен истечь посреди теста.
        builder.UseSetting("Auth:TokenHours", "720");
        // Автосписание в тестах вызывается явно, фоновый запуск мешал бы сценариям с переводом часов.
        builder.UseSetting("Stock:AutoWriteOff", "false");
        builder.UseSetting("Stock:TrackStops", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Api = await SignInAsync("demo");
        Waiter = await SignInAsync("waiter");
    }

    /// <summary>Клиент, вошедший под одной из демо-записей: demo (шеф-повар), manager, cook, storekeeper, waiter.</summary>
    public const string SupportPassword = "support-tests-2026";

    /// <summary>Закрытая учётная запись команды продукта: только она видит заявки и вопросы с лендинга.</summary>
    public Task<HttpClient> SignInAsSupportAsync() => SignInAsync("support", SupportPassword);

    public async Task<HttpClient> SignInAsync(string login, string password = "demo2026")
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Область служб, работающая от имени демо-ресторана, — как фоновая задача сервера.</summary>
    public AsyncServiceScope DemoScope()
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Ostatok.Api.Data.Tenant>().Id = Ostatok.Api.Domain.Restaurant.DemoId;
        return scope;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
