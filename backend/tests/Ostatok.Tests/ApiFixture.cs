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

    public FakeTelegram Telegram { get; } = new();
    public FakeInvoiceReader Invoices { get; } = new();

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
        // Печать журнала тесты вызывают сами: фоновая печать запечатала бы записи посреди сценария.
        builder.UseSetting("Journal:Seal", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            // Настоящий Telegram в тестах заменён записывающей заглушкой.
            services.RemoveAll<ITelegramApi>();
            services.AddSingleton<ITelegramApi>(Telegram);
            services.RemoveAll<IInvoiceReader>();
            services.AddSingleton<IInvoiceReader>(Invoices);
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokens = new();

    public async Task<HttpClient> SignInAsync(string login, string password = "demo2026")
    {
        var client = CreateClient();
        // Сервер принимает не больше 60 входов в минуту с адреса. Демо-записи и support в тестах не меняются,
        // поэтому их токен берётся один раз: иначе сотня тестов упирается в этот предел.
        var known = password is "demo2026" or SupportPassword;
        if (!known || !_tokens.TryGetValue(login, out var token))
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password));
            response.EnsureSuccessStatusCode();
            token = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!.Token;
            if (known) _tokens[login] = token;
        }
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

    /// <summary>Создаёт ресторан так, как это делает поддержка, и возвращает клиент его управляющего.</summary>
    public async Task<(HttpClient Client, string Login, string Password)> NewRestaurantAsync(string name)
    {
        using var support = await SignInAsSupportAsync();
        var login = $"o{Guid.NewGuid():N}"[..20];
        var response = await support.PostAsJsonAsync("/api/restaurants", new RestaurantCreateRequest(name, login));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<NewRestaurant>(Json))!;
        return (await SignInAsync(created.Login, created.Password), created.Login, created.Password);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>Заглушка Telegram: запоминает, что бот отправил бы операторам.</summary>
/// <summary>Вместо модели: возвращает заранее заданную таблицу, как будто её прочитали с фото.</summary>
public sealed class FakeInvoiceReader : IInvoiceReader
{
    public bool Enabled { get; set; } = true;
    public List<string[]> Table { get; set; } = [];

    public Task<List<string[]>> ReadAsync(byte[] image, string mime, IReadOnlyCollection<Ostatok.Api.Domain.Product> products, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(Table);
}

public sealed class FakeTelegram : ITelegramApi
{
    public List<(long ChatId, string Text)> Sent { get; } = [];
    public bool Enabled => true;

    public Task SendAsync(long chatId, string text, CancellationToken ct)
    {
        lock (Sent) Sent.Add((chatId, text));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct).ContinueWith<IReadOnlyList<TelegramUpdate>>(_ => [], CancellationToken.None);

    public string[] To(long chatId)
    {
        lock (Sent) return Sent.Where(m => m.ChatId == chatId).Select(m => m.Text).ToArray();
    }
}
