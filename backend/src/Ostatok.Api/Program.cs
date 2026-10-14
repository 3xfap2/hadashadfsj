using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ostatok.Api.Data;
using Ostatok.Api.Features;

var builder = WebApplication.CreateBuilder(args);

// Ключ шифрования личных данных; отдельной настройки может не быть — тогда он выводится из секрета токенов.
PersonalData.Configure(builder.Configuration["Crypto:Key"] ?? builder.Configuration["Auth:Key"] ?? "");

builder.Services.AddSingleton<ReadCache>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddScoped<Tenant>();
builder.Services.AddSingleton<PublishChanges>();
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(ConnectionString(sp.GetRequiredService<IConfiguration>().GetConnectionString("Db")))
    .AddInterceptors(sp.GetRequiredService<PublishChanges>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PrepService>();
builder.Services.AddScoped<SupportDesk>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Telegram").Get<TelegramOptions>() ?? new TelegramOptions());
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Ai").Get<AiOptions>() ?? new AiOptions());
builder.Services.AddSingleton<IInvoiceReader, OpenAiInvoiceReader>();
builder.Services.AddSingleton<PhotoQuota>();
builder.Services.AddSingleton<ITelegramApi, TelegramApi>();
builder.Services.AddHostedService<TelegramBot>();
builder.Services.AddScoped<WriteOffService>();
builder.Services.AddScoped<StopTracker>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddSingleton<SimulatorState>();
builder.Services.AddHostedService<Simulator>();
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Stock").Get<StockOptions>() ?? new StockOptions());
builder.Services.AddHostedService<ExpiryWorker>();

builder.Services.AddSingleton(sp =>
{
    var auth = sp.GetRequiredService<IConfiguration>().GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
    if (auth.Key.Length < 32) throw new InvalidOperationException("Задайте Auth:Key — секрет не короче 32 символов.");
    // Ключ из настроек разработки лежит в репозитории: на сервере с ним токен мог бы подделать кто угодно.
    if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment() && auth.Key.Contains("development", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Auth:Key взят из настроек разработки. Задайте свой секрет переменной окружения Auth__Key.");
    return auth;
});
builder.Services.AddSingleton<LoginAttempts>();
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Restaurant").Get<RestaurantOptions>() ?? new RestaurantOptions());
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>>(sp =>
    new ConfigureNamedOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var time = sp.GetRequiredService<TimeProvider>();
        // Поток событий открывает сам браузер и не может добавить заголовок — токен для него идёт в адресе.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments(RealtimeEndpoints.Path) && context.Request.Query.TryGetValue("access_token", out var token))
                    context.Token = token;
                return Task.CompletedTask;
            },
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = AuthOptions.Issuer,
            ValidAudience = AuthOptions.Issuer,
            IssuerSigningKey = sp.GetRequiredService<AuthOptions>().SigningKey,
            // Принимается только тот алгоритм подписи, которым токены выдаются.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            // Срок токена проверяется по тем же часам, по которым он выдан.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return notBefore <= now && now < expires;
            },
        };
    }));
builder.Services.AddAuthorization();

// Общий предел запросов на вход с одного адреса; подбор пароля к логину отсекает LoginAttempts.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(LeadEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // Открытый чат лендинга раз в несколько секунд спрашивает, нет ли ответа, поэтому предел выше.
    // Не больше десяти сообщений в минуту с одного адреса: чат нельзя завалить сотней диалогов.
    options.AddPolicy(ChatEndpoints.WriteRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = ChatEndpoints.MessagesPerMinute, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(ChatEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    // Приложение перед каждым запросом спрашивает разрешение; ответ запоминается на час, а не на секунды.
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));

builder.Services.ConfigureHttpJsonOptions(options =>
    // Перечисления — только названием: число 0 иначе молча превращалось бы в первую роль или единицу.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddExceptionHandler<CorsOnErrors>();
builder.Services.AddExceptionHandler<DbErrors>();
builder.Services.AddProblemDetails();
// Запрос, который не удалось разобрать (дробь вместо целого, оборванный JSON), — ошибка клиента (400),
// одинаково в разработке и на сервере.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddOpenApi();

// Тело запроса: самое большое, что принимает API, — фото блюда до 10 МБ.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 12_000_000);

var app = builder.Build();

// За обратным прокси хостинга адрес клиента приходит в заголовке X-Forwarded-For. Доверять ему можно,
// только если перед сервером действительно стоит прокси: иначе заголовок подделывается и обходит ограничения частоты.
if (app.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders"))
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
// Заголовки защиты: не угадывать тип содержимого, не встраивать API во фреймы, не передавать адрес страницы
// другим сайтам (в адресе потока событий есть токен). Страницы сайта встраивает только Telegram:
// в веб-версии и на компьютере мини-приложение бота открывается во фрейме.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    if (context.Request.Path.StartsWithSegments("/api")) headers.XFrameOptions = "DENY";
    else headers.ContentSecurityPolicy = "frame-ancestors 'self' https://web.telegram.org";
    headers["Referrer-Policy"] = "no-referrer";
    // Ответы API с данными ресторана не должны оседать в кэше браузера и прокси; фото блюд кэшировать можно.
    // Файлы самого сайта кэшируются как обычно: в их именах есть отпечаток содержимого.
    if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.Value!.EndsWith("/photo", StringComparison.Ordinal))
        headers.CacheControl = "no-store";
    await next();
});
// В собранном образе рядом с API лежит сайт (папка wwwroot): сервер отдаёт его сам.
// Файлы раздаются до выбора маршрута: иначе запрос скрипта или фото перехватил бы маршрут «любая страница сайта».
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Ресторан берётся из токена: дальше все запросы к базе видят только его данные.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var db = context.RequestServices.GetRequiredService<AppDbContext>();
        if (!await AuthEndpoints.IsCurrentAsync(context.User, db, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.RequestServices.GetRequiredService<Tenant>().Id = Guid.Parse(context.User.FindFirstValue(AuthEndpoints.RestaurantClaim)!);
    }
    await next();
});

app.MapOpenApi();
// Счётчики кэша чтений — без данных ресторанов, поэтому открыты, как и сама проверка.
app.MapGet("/health", (ReadCache cache) => Results.Ok(new { status = "ok", cache = cache.Stats() })).ExcludeFromDescription();
app.MapAuth();
app.MapLeads();
app.MapChats();
app.MapGuestMenu();
app.MapRealtime();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapCatalog();
api.MapSupplies();
api.MapOrders();
api.MapPreps();
api.MapStock();
api.MapInventory();
api.MapInsights();
api.MapHotSettings();
api.MapAlerts();
api.MapSimulator();
api.MapStaff();
api.MapRestaurant();
api.MapOwner();
api.MapOwnerCabinet();
api.MapOwnerMessages();
api.MapSupportDesk();
api.MapCrm();

await using (var scope = app.Services.CreateAsyncScope())
{
    // Начальные данные принадлежат демо-ресторану.
    scope.ServiceProvider.GetRequiredService<Tenant>().Id = Ostatok.Api.Domain.Restaurant.DemoId;
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await AuthEndpoints.EnsureUsersAsync(db, scope.ServiceProvider.GetRequiredService<AuthOptions>(), scope.ServiceProvider.GetRequiredService<TimeProvider>());
    if (app.Configuration.GetValue<bool>("Seed:Demo"))
    {
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        await DemoSeed.RunAsync(db, now);
        // Сервер на бесплатном хостинге засыпает и просыпается часто: при запуске опустевший демо-склад пополняется.
        await DemoSeed.RefreshAsync(db, now);
    }
}

// CRM команды продукта — второе приложение, в папке crm. Его маршрут точнее общего, поэтому адреса /crm/... достаются ему;
// сам адрес /crm/ отдаёт UseDefaultFiles.
app.MapFallbackToFile("crm/{*path:regex(^(?!.*[.][a-z0-9]+$).*$)}", "crm/index.html");
// Кабинет владельца ресторанов — третье приложение, в папке owner; подключено так же, как CRM.
app.MapFallbackToFile("owner/{*path:regex(^(?!.*[.][a-z0-9]+$).*$)}", "owner/index.html");
// Страницы сайта — одно приложение: любой адрес, кроме API, открывает его главную страницу.
app.MapFallbackToFile("{*path:regex(^(?!api/|openapi/|health$)(?!.*[.][a-z0-9]+$).*$)}", "index.html");

app.Run();

// Хостинги баз данных дают адрес вида postgres://пользователь:пароль@сервер/база — он переводится в формат Npgsql.
static string? ConnectionString(string? value)
{
    if (value is null || !value.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)) return value;
    var uri = new Uri(value);
    var user = uri.UserInfo.Split(':', 2);
    return new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(user[0]),
        Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "",
        SslMode = Npgsql.SslMode.Require,
    }.ConnectionString;
}

public partial class Program;
