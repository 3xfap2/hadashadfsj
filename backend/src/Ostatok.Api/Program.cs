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

builder.Services.AddSingleton<EventBus>();
builder.Services.AddScoped<Tenant>();
builder.Services.AddSingleton<PublishChanges>();
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Db"))
    .AddInterceptors(sp.GetRequiredService<PublishChanges>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PrepService>();
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
    options.AddPolicy(ChatEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
// Заголовки защиты: не угадывать тип содержимого, не встраивать API во фреймы, не передавать адрес страницы
// другим сайтам (в адресе потока событий есть токен).
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    // Ответы API с данными ресторана не должны оседать в кэше браузера и прокси; фото блюд кэшировать можно.
    if (!context.Request.Path.Value!.EndsWith("/photo", StringComparison.Ordinal)) headers.CacheControl = "no-store";
    await next();
});
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
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapAuth();
app.MapLeads();
app.MapChats();
app.MapRealtime();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapCatalog();
api.MapSupplies();
api.MapOrders();
api.MapPreps();
api.MapStock();
api.MapInsights();
api.MapHotSettings();
api.MapAlerts();
api.MapSimulator();
api.MapStaff();
api.MapRestaurant();

await using (var scope = app.Services.CreateAsyncScope())
{
    // Начальные данные принадлежат демо-ресторану.
    scope.ServiceProvider.GetRequiredService<Tenant>().Id = Ostatok.Api.Domain.Restaurant.DemoId;
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await AuthEndpoints.EnsureUsersAsync(db, scope.ServiceProvider.GetRequiredService<AuthOptions>(), scope.ServiceProvider.GetRequiredService<TimeProvider>());
    if (app.Configuration.GetValue<bool>("Seed:Demo"))
        await DemoSeed.RunAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
}

app.Run();

public partial class Program;
