using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;

namespace Ostatok.Api.Features;

/// <summary>Сведения о сборке и времени запуска для /health.</summary>
public sealed record HealthInfo(string Version, DateTimeOffset StartedAt);

/// <summary>
/// Наблюдаемость: проверки работоспособности и номер запроса.
/// /health — без базы и мгновенно: им пользуется пингер, который не даёт бесплатному хостингу усыпить сервер.
/// /health/ready — с запросом к базе: 503, если она недоступна.
/// </summary>
public static class Observability
{
    public const string RequestIdHeader = "X-Request-Id";

    /// <summary>Версия — коммит, из которого собран сервер: Render задаёт RENDER_GIT_COMMIT сам, при сборке образа можно передать GIT_SHA.</summary>
    public static HealthInfo Info(IConfiguration config, TimeProvider time) => new(
        new[] { config["RENDER_GIT_COMMIT"], config["GIT_SHA"] }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "local",
        time.GetUtcNow());

    public static void MapHealth(this WebApplication app)
    {
        var info = Info(app.Configuration, app.Services.GetRequiredService<TimeProvider>());
        // Счётчики кэша чтений — без данных ресторанов, поэтому открыты, как и сама проверка.
        app.MapGet("/health", (ReadCache cache) => Results.Ok(new { status = "ok", info.Version, info.StartedAt, cache = cache.Stats() }))
            .ExcludeFromDescription();
        app.MapGet("/health/ready", async (AppDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            // Дольше трёх секунд база не отвечает — считаем недоступной: проверка не должна висеть вместе с ней.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").SingleAsync(timeout.Token);
                return Results.Ok(new { status = "ready", info.Version });
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logs.CreateLogger("Ostatok.Health").LogWarning(e, "База недоступна");
                return Results.Json(new { status = "unavailable", info.Version }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).ExcludeFromDescription();
    }

    /// <summary>
    /// Номер запроса: берётся из входящего X-Request-Id (его ставит прокси или клиент) или создаётся заново,
    /// возвращается в ответе и попадает в область журнала — по нему находятся все строки журнала одного запроса.
    /// В конце запроса — одна строка: метод, адрес, код ответа и время.
    /// </summary>
    public static void UseRequestId(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ostatok.Requests");
        app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[RequestIdHeader].ToString();
            var id = IsValid(incoming) ? incoming : Guid.NewGuid().ToString("N");
            context.TraceIdentifier = id;
            // Заголовок ставится перед отправкой: обработчик ошибок очищает заголовки ответа, а номер нужен и в ответе 500.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[RequestIdHeader] = id;
                return Task.CompletedTask;
            });
            using var scope = log.BeginScope("X-Request-Id:{XRequestId}", id);
            var started = Stopwatch.GetTimestamp();
            await next();
            log.LogInformation("{Method} {Path} → {Status} за {Elapsed:0} мс", context.Request.Method, context.Request.Path, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        });
    }

    // Чужой номер принимается, только если он короткий и из безопасных символов: он попадает в журнал и в заголовок ответа.
    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
