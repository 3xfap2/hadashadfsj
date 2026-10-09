using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ostatok.Api.Data;
using Ostatok.Api.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Db")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<WriteOffService>();
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
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = AuthOptions.Issuer,
            ValidAudience = AuthOptions.Issuer,
            IssuerSigningKey = sp.GetRequiredService<AuthOptions>().SigningKey,
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
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapAuth();
app.MapLeads();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapCatalog();
api.MapSupplies();
api.MapOrders();
api.MapStock();
api.MapInsights();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await AuthEndpoints.EnsureUserAsync(db, scope.ServiceProvider.GetRequiredService<AuthOptions>());
    if (app.Configuration.GetValue<bool>("Seed:Demo"))
        await DemoSeed.RunAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
}

app.Run();

public partial class Program;
