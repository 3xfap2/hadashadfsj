using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="AutoWriteOff">Списывать просрочку без человека; пусто в запросе — не менять.</param>
/// <param name="AutoWriteOffAfterHours">Через сколько часов после истечения срока спишется сама; пусто в запросе — не менять.</param>
/// <param name="HasIntegrationKey">Есть ли у ресторана хотя бы один действующий ключ внешней системы; в запросе не учитывается.</param>
public sealed record RestaurantSettings(
    string? Name, string? ShiftStart, string? ShiftEnd, int Tables, bool HasBar,
    bool? AutoWriteOff = null, int? AutoWriteOffAfterHours = null, bool HasIntegrationKey = false);

/// <param name="Key">Ключ показывается один раз: в базе остаётся только его хеш.</param>
public sealed record IntegrationKeyResponse(string Key, Guid RestaurantId);

/// <param name="Name">Чей ключ: «Поставщик ФрешПоставка», «Касса зала»; до 60 символов. Это имя стоит в журнале как исполнитель.</param>
/// <param name="Rights">Права: supplies — присылать входящие поставки, orders — читать меню и оформлять заказы. Можно оба.</param>
public sealed record ApiKeyRequest(string? Name, List<string>? Rights);

/// <param name="CanRevoke">Можно ли отозвать ключ сейчас (в демо-ресторане — только в первые 30 минут после выдачи).</param>
public sealed record ApiKeyRow(
    Guid Id, string Name, IEnumerable<string> Rights, DateTimeOffset CreatedAt, string CreatedBy,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string? RevokedBy, bool CanRevoke);

/// <param name="Key">Ключ показывается один раз: в базе остаётся только его хеш.</param>
public sealed record ApiKeyIssued(string Key, Guid Id, Guid RestaurantId, string Name, IEnumerable<string> Rights);

/// <summary>Настройки ресторана: название, часы смены и зал. У каждого ресторана они свои.</summary>
public static partial class RestaurantEndpoints
{
    [GeneratedRegex(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$|^24:00$")]
    private static partial Regex ClockTime();

    public static void MapRestaurant(this RouteGroupBuilder api)
    {
        var restaurant = api.MapGroup("/restaurant").WithTags("Ресторан").RequireRoles(Access.Staff);

        restaurant.MapGet("", async (AppDbContext db, CancellationToken ct) => await ToSettingsAsync(await db.CurrentRestaurantAsync(ct), db, ct));

        restaurant.MapPut("", async (RestaurantSettings req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            // «9:00» — то же, что «09:00»: в базе время хранится в одном виде.
            var (name, start, end) = ((req.Name ?? "").Trim(), (req.ShiftStart ?? "").Trim().PadLeft(5, '0'), (req.ShiftEnd ?? "").Trim().PadLeft(5, '0'));
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "name", "Укажите название ресторана — до 120 символов.")
                .Check(ClockTime().IsMatch(start) && ClockTime().IsMatch(end) && start != "24:00", "shift", "Время смены — в формате ЧЧ:ММ: начало не позже 23:59, конец не позже 24:00.")
                .Check(req.Tables is >= 0 and <= 60, "tables", "Столов — от 0 до 60.")
                .Check(req.Tables > 0 || req.HasBar, "tables", "В зале должен быть хотя бы один стол или бар.")
                .Check(req.AutoWriteOffAfterHours is null or (>= 1 and <= 168), "autoWriteOffAfterHours", "Отсрочка автосписания — от 1 до 168 часов: так просрочку успевают увидеть и списать вручную.");
            if (errors.Any) return errors.ToResult();
            // Конец раньше начала — смена через полночь (18:00–02:00), это допустимо; совпадать они не могут.
            if (StopCost.ParseTime(end) == StopCost.ParseTime(start))
                return errors.Add("shift", "Начало и конец смены не должны совпадать.").ToResult();

            // Столы схемы зала принимаются заказом как «Стол N»: число столов не может быть меньше самого большого номера в схеме.
            var maxHallTable = await db.HallTables.Where(t => !t.Archived).MaxAsync(t => (int?)t.Number, ct) ?? 0;
            if (req.Tables < maxHallTable)
                return errors.Add("tables", $"В схеме зала есть стол № {maxHallTable}: столов должно быть не меньше {maxHallTable}. Уберите стол в «Зал → Настройка» или увеличьте число.").ToResult();

            var places = new Restaurant { Name = name, Tables = req.Tables, HasBar = req.HasBar }.Places();
            var busy = await OrderEndpoints.OpenOrders(db, time.GetUtcNow()).Select(o => o.Place).Distinct().ToListAsync(ct);
            if (busy.Except(places).Order().ToList() is { Count: > 0 } left)
                return errors.Add("tables", $"Сначала закройте открытые заказы: {string.Join(", ", left)}.").ToResult();

            var row = await db.Restaurants.FirstAsync(r => r.Id == db.TenantId, ct);
            (row.Name, row.ShiftStart, row.ShiftEnd, row.Tables, row.HasBar) = (name, start, end, req.Tables, req.HasBar);
            if (req.AutoWriteOff is { } auto) row.AutoWriteOff = auto;
            if (req.AutoWriteOffAfterHours is { } hours) row.AutoWriteOffAfterHours = hours;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ToSettingsAsync(row, db, ct));
        });

        // Ключи внешних систем: у каждой системы (поставщик, касса, сайт кафе) свой ключ со своими правами.
        // Ключ показывается один раз; отозванный ключ сразу перестаёт работать, остальные не затрагиваются.
        var keys = restaurant.MapGroup("/keys");

        keys.MapGet("", async (AppDbContext db, TimeProvider time, CancellationToken ct) =>
            (await db.ApiKeys.OrderBy(k => k.RevokedAt != null).ThenByDescending(k => k.CreatedAt).Take(100).ToListAsync(ct))
            .Select(k => ToRow(k, db.TenantId, time.GetUtcNow())))
            .WithSummary("Ключи внешних систем ресторана")
            .WithDescription("Действующие и отозванные ключи: название, права, кто и когда выдал, когда ключом пользовались последний раз. Сам ключ не возвращается — он показывается один раз при выдаче.");

        keys.MapPost("", async (ApiKeyRequest req, AppDbContext db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct) =>
        {
            var name = (req.Name ?? "").Trim();
            var rights = (req.Rights ?? []).Select(r => r.Trim().ToLowerInvariant()).Distinct().ToList();
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 60 }, "name", "Укажите, чей это ключ: «Поставщик ФрешПоставка», «Касса зала» — до 60 символов.")
                .Check(rights.Count > 0, "rights", "Выберите хотя бы одно право: supplies — присылать поставки, orders — оформлять заказы.")
                .Check(rights.All(IntegrationKey.Rights.Contains), "rights", "Права ключа — только supplies и orders.");
            if (errors.Any) return errors.ToResult();
            if (await db.ApiKeys.CountAsync(k => k.RevokedAt == null, ct) >= IntegrationKey.MaxActive)
                return Results.Problem($"У ресторана уже {IntegrationKey.MaxActive} действующих ключей. Отзовите ненужные.", statusCode: StatusCodes.Status409Conflict);

            var (row, key) = IntegrationKey.New(name, rights.Contains(IntegrationKey.Supplies), rights.Contains(IntegrationKey.Orders), user.Identity?.Name ?? "", time.GetUtcNow());
            db.ApiKeys.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/restaurant/keys/{row.Id}", new ApiKeyIssued(key, row.Id, db.TenantId, row.Name, IntegrationKey.RightsOf(row)));
        })
        .Produces<ApiKeyIssued>(StatusCodes.Status201Created)
        .WithSummary("Выдать ключ внешней системе")
        .WithDescription("Права: supplies — присылать входящие поставки (POST /api/supplies/incoming); orders — читать меню со стоп-листом и оформлять заказы (GET /api/integration/menu, POST /api/orders/external). Ключ возвращается один раз, в базе остаётся только его SHA-256. Не больше 20 действующих ключей на ресторан.");

        keys.MapPost("{id:guid}/revoke", async (Guid id, AppDbContext db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct) =>
        {
            var row = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
            if (row is null) return Results.NotFound();
            var now = time.GetUtcNow();
            if (row.RevokedAt is null)
            {
                if (!IntegrationKey.CanRevoke(row, db.TenantId, now)) return IntegrationKey.DemoLocked();
                (row.RevokedAt, row.RevokedBy) = (now, user.Identity?.Name ?? "");
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        })
        .WithSummary("Отозвать ключ")
        .WithDescription("Ключ сразу перестаёт работать; остальные ключи ресторана не затрагиваются. В открытом демо-ресторане можно отозвать только ключ, выданный не раньше 30 минут назад: ключ для показа посетители отозвать не могут.");

        // Прежний способ: один «Ключ поставщика» с правом присылать поставки; новый заменяет прежний ключ с тем же названием.
        // Оставлен для совместимости, кабинет выдаёт ключи через /keys.
        restaurant.MapPost("/integration-key", async (AppDbContext db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var previous = await db.ApiKeys.Where(k => k.Name == IntegrationKey.SupplierKeyName && k.RevokedAt == null).ToListAsync(ct);
            if (previous.Any(k => !IntegrationKey.CanRevoke(k, db.TenantId, now))) return IntegrationKey.DemoLocked();
            foreach (var k in previous) (k.RevokedAt, k.RevokedBy) = (now, user.Identity?.Name ?? "");
            var (row, key) = IntegrationKey.New(IntegrationKey.SupplierKeyName, true, false, user.Identity?.Name ?? "", now);
            db.ApiKeys.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new IntegrationKeyResponse(key, db.TenantId));
        })
        .WithSummary("Выдать ключ поставщика (прежний способ)")
        .WithDescription("Выдаёт ключ «Ключ поставщика» с правом supplies и отзывает прежний ключ с этим названием. Новые интеграции — через POST /api/restaurant/keys.");
    }

    private static async Task<RestaurantSettings> ToSettingsAsync(Restaurant r, AppDbContext db, CancellationToken ct) =>
        new(r.Name, r.ShiftStart, r.ShiftEnd, r.Tables, r.HasBar, r.AutoWriteOff, r.AutoWriteOffAfterHours,
            await db.ApiKeys.AnyAsync(k => k.RevokedAt == null, ct));

    private static ApiKeyRow ToRow(ApiKey k, Guid restaurant, DateTimeOffset now) =>
        new(k.Id, k.Name, IntegrationKey.RightsOf(k), k.CreatedAt, k.CreatedBy, k.LastUsedAt, k.RevokedAt, k.RevokedBy,
            k.RevokedAt is null && IntegrationKey.CanRevoke(k, restaurant, now));
}

/// <summary>
/// Ключи внешних систем ресторана. Хранится только SHA-256: ключ случайный и длинный, подбирать его бессмысленно.
/// Ресторан запроса определяется ключом, а не входом сотрудника.
/// </summary>
public static class IntegrationKey
{
    public const string Header = "X-Api-Key";
    public const string Supplies = "supplies";
    public const string Orders = "orders";
    public static readonly string[] Rights = [Supplies, Orders];
    public const string SupplierKeyName = "Ключ поставщика";
    public const int MaxActive = 20;
    /// <summary>Запросов в минуту с одного ключа. Счётчик живёт в памяти процесса: при нескольких копиях сервера предел у каждой свой.</summary>
    public const int PerMinute = 60;
    /// <summary>В открытом демо-ресторане ключ можно отозвать только в первые полчаса: посетитель убирает свой ключ, но не ключ для показа.</summary>
    public static readonly TimeSpan DemoRevokeWindow = TimeSpan.FromMinutes(30);
    private const string ItemKey = "ostatok.api-key";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (long Minute, int Count)> Hits = new();

    public static string Create() => "ost_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    public static (ApiKey Row, string Key) New(string name, bool supplies, bool orders, string by, DateTimeOffset now)
    {
        var key = Create();
        return (new ApiKey { Id = Guid.NewGuid(), Name = name, Hash = Hash(key), CanSupplies = supplies, CanOrders = orders, CreatedAt = now, CreatedBy = by }, key);
    }

    public static IEnumerable<string> RightsOf(ApiKey k) => Rights.Where(r => r == Supplies ? k.CanSupplies : k.CanOrders).ToList();

    public static bool CanRevoke(ApiKey k, Guid restaurant, DateTimeOffset now) =>
        restaurant != Restaurant.DemoId || now - k.CreatedAt <= DemoRevokeWindow;

    public static IResult DemoLocked() => Results.Problem(
        "Это открытый демо-ресторан: ключ, выданный больше 30 минут назад, отозвать нельзя — им пользуются на показе. Выдайте себе отдельный ключ.",
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Ключ, которым авторизован запрос (после <see cref="AuthorizeAsync"/> или фильтра <see cref="RequireKey"/>).</summary>
    public static ApiKey Current(HttpContext http) => (ApiKey)http.Items[ItemKey]!;

    /// <summary>
    /// Проверяет ключ из заголовка X-Api-Key и право на действие; при успехе запрос дальше работает от имени ресторана ключа.
    /// Нет ключа, он неверный или отозван, ресторан отключён — 401; нет права — 403; больше 60 запросов в минуту — 429.
    /// null — всё в порядке.
    /// </summary>
    public static async Task<IResult?> AuthorizeAsync(HttpContext http, string? right, CancellationToken ct)
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        string? key = http.Request.Headers[Header];
        var hash = string.IsNullOrWhiteSpace(key) || key.Length > 100 ? null : Hash(key);
        // Ресторан ещё не известен, поэтому фильтр по ресторану снят: ключ ищется по отпечатку среди всех.
        var row = hash is null ? null : await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.Hash == hash && k.RevokedAt == null && db.Restaurants.Any(r => r.Id == k.RestaurantId && r.DisabledAt == null))
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return Results.Problem($"Нужен действующий ключ интеграции ресторана в заголовке {Header}. Ключ выдаёт управляющий в разделе «Ресторан».",
                statusCode: StatusCodes.Status401Unauthorized);

        var minute = now.ToUnixTimeSeconds() / 60;
        var hits = Hits.AddOrUpdate(row.Id, (minute, 1), (_, h) => h.Minute == minute ? (minute, h.Count + 1) : (minute, 1));
        if (hits.Count > PerMinute)
        {
            http.Response.Headers.RetryAfter = (60 - now.ToUnixTimeSeconds() % 60).ToString();
            return Results.Problem($"С ключа «{row.Name}» больше {PerMinute} запросов в минуту. Повторите чуть позже.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (right is not null && !RightsOf(row).Contains(right))
            return Results.Problem(
                $"Ключ «{row.Name}» не даёт права {(right == Supplies ? "присылать поставки" : "оформлять заказы и читать меню")}. Управляющий может выдать ключ с этим правом в разделе «Ресторан».",
                statusCode: StatusCodes.Status403Forbidden);

        services.GetRequiredService<Tenant>().Id = row.RestaurantId;
        http.Items[ItemKey] = row;
        // Время последнего использования пишется не чаще раза в минуту: ключ кассы не должен писать в базу на каждый запрос.
        if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(1))
            await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, now), ct);
        return null;
    }

    /// <summary>
    /// Метод только для внешних систем: вход сотрудника не нужен и не учитывается, нужен ключ с правом <paramref name="right"/>
    /// (null — с любым правом).
    /// </summary>
    public static RouteHandlerBuilder RequireKey(this RouteHandlerBuilder builder, string? right) => builder
        .AllowAnonymous()
        .AddEndpointFilter(async (context, next) => await AuthorizeAsync(context.HttpContext, right, context.HttpContext.RequestAborted) ?? await next(context))
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .WithKeySecurity();

    /// <summary>Отмечает метод в описании API схемой безопасности «ключ в заголовке X-Api-Key».</summary>
    public static TBuilder WithKeySecurity<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddOpenApiOperationTransformer((operation, context, ct) =>
        {
            var document = context.Document!;
            document.Components ??= new();
            document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes.TryAdd(Header, new Microsoft.OpenApi.OpenApiSecurityScheme
            {
                Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
                In = Microsoft.OpenApi.ParameterLocation.Header,
                Name = Header,
                Description = "Ключ внешней системы ресторана. Выдаёт управляющий: «Ресторан» → «Ключи внешних систем». Права: supplies, orders.",
            });
            operation.Security ??= [];
            operation.Security.Add(new Microsoft.OpenApi.OpenApiSecurityRequirement { [new Microsoft.OpenApi.OpenApiSecuritySchemeReference(Header, document)] = [] });
            return Task.CompletedTask;
        });
}
