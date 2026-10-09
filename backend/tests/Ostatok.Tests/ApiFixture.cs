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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Db", _postgres.GetConnectionString());
        builder.UseSetting("Auth:Key", "integration-tests-signing-key-0123456789");
        builder.UseSetting("Seed:Demo", "false");
        // Автосписание в тестах вызывается явно, фоновый запуск мешал бы сценариям с переводом часов.
        builder.UseSetting("Stock:AutoWriteOff", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Api = CreateClient();
        var login = await Api.PostAsJsonAsync("/api/auth/login", new LoginRequest("demo", "demo2026"));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>(Json))!.Token;
        Api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
