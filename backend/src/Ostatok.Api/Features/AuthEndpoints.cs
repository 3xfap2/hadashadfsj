using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed class AuthOptions
{
    public const string Issuer = "ostatok";

    /// <summary>Секрет подписи токенов, не короче 32 символов. Задаётся переменной окружения Auth__Key.</summary>
    public string Key { get; set; } = "";
    public int TokenHours { get; set; } = 12;
    /// <summary>Учётные записи, которые создаются при запуске; пароль у всех — DemoPassword.</summary>
    public List<DemoUser> Users { get; set; } = [];
    public string DemoPassword { get; set; } = "";
    /// <summary>Сколько неверных паролей подряд допускается для одного логина.</summary>
    /// <summary>
    /// Закрытая учётная запись команды продукта: только ей видны заявки и вопросы с лендинга.
    /// Пароль задаётся переменной окружения Auth__SupportPassword; без него запись не создаётся.
    /// </summary>
    public string SupportLogin { get; set; } = "support";
    public string SupportPassword { get; set; } = "";
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 10;
    /// <summary>Сколько неверных паролей к одному логину допускается со всех адресов вместе, и на сколько минут он тогда закрывается.</summary>
    public int MaxFailedPerLogin { get; set; } = 20;
    public int LoginLockoutMinutes { get; set; } = 2;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public sealed class DemoUser
{
    public string Login { get; set; } = "";
    public string Title { get; set; } = "";
    public Role Role { get; set; }
}

public sealed record LoginRequest(string? Login, string? Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record ShiftInfo(string Start, string End);
public sealed record MeResponse(
    string Login, string Title, Role Role, IEnumerable<string> Sections, ShiftInfo Shift, IEnumerable<string> Places, string Restaurant, Guid RestaurantId);


/// <summary>Часы смены, с которыми создаётся новый ресторан; дальше каждый ресторан настраивает свои.</summary>
public sealed class RestaurantOptions
{
    public string ShiftStart { get; set; } = "12:00";
    public string ShiftEnd { get; set; } = "24:00";
}

/// <summary>
/// Защита от подбора пароля: после нескольких неверных попыток подряд логин блокируется на время.
/// Счёт ведётся по введённому логину, а не по учётной записи, поэтому ответ одинаков
/// для существующих и несуществующих логинов и не выдаёт, какие из них заняты.
/// Надолго блокируется пара «логин + адрес»: чужие неверные попытки не закроют вход настоящему сотруднику.
/// Отдельно считаются неверные попытки к логину со всех адресов: подбор с многих адресов упирается
/// в короткую блокировку логина, которая сама снимается через пару минут.
/// </summary>
public sealed class LoginAttempts(AuthOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (int Failed, DateTimeOffset LockedUntil)> _state = new();

    private const int MaxTracked = 50_000;
    /// <summary>Адрес счётчика «со всех адресов»; настоящий адрес звёздочкой не бывает.</summary>
    private const string AnyAddress = "*";

    private static string Key(string? login, string address) => $"{(login ?? "").Trim().ToLowerInvariant()}|{address}";

    /// <summary>Сколько минут осталось до разблокировки; 0 — вход разрешён.</summary>
    public int LockedMinutes(string? login, string address) => Math.Max(Left(Key(login, address)), Left(Key(login, AnyAddress)));

    private int Left(string key)
    {
        if (!_state.TryGetValue(key, out var s)) return 0;
        var left = s.LockedUntil - time.GetUtcNow();
        return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalMinutes) : 0;
    }

    /// <summary>
    /// Учитывает неверную попытку и возвращает, сколько попыток осталось. Меньше нуля — вход уже был закрыт до этой попытки:
    /// так бывает, когда пачка одновременных запросов прошла проверку блокировки раньше, чем засчитана первая неудача.
    /// </summary>
    public int Fail(string? login, string address)
    {
        // Перебор случайных логинов не должен занять всю память: старые записи сбрасываются.
        if (_state.Count > MaxTracked) _state.Clear();
        // Пароль демо-записей опубликован, подбирать его незачем, а закрыть демо-вход для всех посетителей было бы легко.
        var any = IsDemo(login) ? 0 : Count(Key(login, AnyAddress), options.MaxFailedPerLogin, options.LoginLockoutMinutes);
        var own = Count(Key(login, address), options.MaxFailedLogins, options.LockoutMinutes);
        return any > options.MaxFailedPerLogin ? -1 : options.MaxFailedLogins - own;
    }

    private int Count(string key, int max, int minutes)
    {
        var now = time.GetUtcNow();
        return _state.AddOrUpdate(key, _ => (1, max <= 1 ? now.AddMinutes(minutes) : default), (_, s) =>
        {
            // Блокировка истекла — счёт начинается заново.
            var failed = s.LockedUntil != default && s.LockedUntil <= now ? 1 : s.Failed + 1;
            return (failed, failed >= max ? now.AddMinutes(minutes) : default);
        }).Failed;
    }

    private bool IsDemo(string? login) => options.Users.Any(u => u.Login == (login ?? "").Trim().ToLowerInvariant());

    /// <summary>Вход удался: счёт пары сбрасывается, а попытка, засчитанная логину заранее, снимается.</summary>
    public void Reset(string? login, string address)
    {
        _state.TryRemove(Key(login, address), out _);
        if (!IsDemo(login)) _state.AddOrUpdate(Key(login, AnyAddress), _ => (0, default), (_, s) => (Math.Max(0, s.Failed - 1), s.LockedUntil));
    }
}

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, "нет такой учётной записи");

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, HttpContext http, AppDbContext db, AuthOptions options, LoginAttempts attempts, TimeProvider time, CancellationToken ct) =>
        {
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            // Логины хранятся строчными; телефон любит ставить заглавную букву и пробел в конце.
            var login = (req.Login ?? "").Trim().ToLowerInvariant();
            if (attempts.LockedMinutes(login, address) is > 0 and var minutes)
                return Locked(minutes);

            // Попытка засчитывается до проверки пароля и снимается при успехе: иначе одновременные запросы прошли бы проверку
            // блокировки все разом, пока долго считается хеш, и подбор шёл бы быстрее пяти попыток.
            var left = attempts.Fail(login, address);
            if (left < 0) return Locked(options.LockoutMinutes);

            // До входа ресторан неизвестен: учётная запись ищется по логину среди всех.
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Login == login, ct);
            var hasher = new PasswordHasher<User>();
            // Для несуществующего логина хеш считается так же долго, как для настоящего: по времени ответа их не различить.
            var matches = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, req.Password ?? "") != PasswordVerificationResult.Failed;
            if (user is null || !matches)
            {
                return left == 0
                    ? Locked(options.LockoutMinutes)
                    : Results.Problem("Неверный логин или пароль.", statusCode: StatusCodes.Status401Unauthorized,
                        extensions: new Dictionary<string, object?> { ["attemptsLeft"] = left });
            }

            attempts.Reset(login, address);
            // Об отключении узнаёт только тот, кто ввёл верный пароль: посторонний по ответу не поймёт, чей это логин.
            if (await db.Restaurants.AnyAsync(r => r.Id == user.RestaurantId && r.DisabledAt != null, ct))
                return Results.Problem("Кабинет ресторана отключён. Напишите в поддержку «Остатка».", statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(Issue(user, options, time));
        })
        .WithTags("Вход")
        .AllowAnonymous()
        .RequireRateLimiting(LoginRateLimit);

        app.MapGet("/api/me", async (ClaimsPrincipal principal, AppDbContext db, AuthOptions options, CancellationToken ct) =>
        {
            // Владелец может работать в ресторане, который добавил сам: его учётная запись заведена в другом, поэтому ищем её среди всех.
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Login == principal.Identity!.Name, ct);
            if (user is null) return Results.Unauthorized();
            var restaurant = await db.CurrentRestaurantAsync(ct);
            // Владелец ресторанов видит ссылку на кабинет владельца, его сотрудники — переписку с ним.
            string[] extra = await OwnerCabinet.IsOwnerAsync(db, user.Login, ct) ? ["owner"]
                : await OwnerMessages.CanWriteToOwnerAsync(db, user.Login, ct) ? ["messages"] : [];
            return Results.Ok(new MeResponse(
                user.Login, user.Title, user.Role, Access.Sections(user.Role, Access.IsVendor(user.Login, restaurant.Id, options)).Concat(extra),
                new ShiftInfo(restaurant.ShiftStart, restaurant.ShiftEnd), restaurant.Places(), restaurant.Name, restaurant.Id));
        })
        .WithTags("Вход")
        .RequireAuthorization();
    }

    public const string RestaurantClaim = "restaurant";
    public const string VersionClaim = "ver";

    /// <summary>
    /// Токен действует, пока учётная запись существует и не менялась: после удаления сотрудника,
    /// смены роли или пароля старый токен перестаёт работать сразу, а не через часы. Так же — после отключения ресторана.
    /// </summary>
    public static Task<bool> IsCurrentAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var login = principal.Identity?.Name;
        if (!Guid.TryParse(principal.FindFirst(RestaurantClaim)?.Value, out var restaurant)
            || !int.TryParse(principal.FindFirst(VersionClaim)?.Value, out var version)
            || !Enum.TryParse<Role>(principal.FindFirst(ClaimTypes.Role)?.Value, out var role)) return Task.FromResult(false);
        // Роль тоже сверяется с базой: токену с подправленной ролью сервер не верит.
        // Ресторан в токене — либо тот, где заведён сотрудник, либо тот, что управляющий добавил себе сам.
        return db.Users.IgnoreQueryFilters().AnyAsync(
            u => u.Login == login && u.TokenVersion == version && u.Role == role
                && (u.RestaurantId == restaurant || (u.Role == Role.Manager && db.Restaurants.Any(r => r.Id == restaurant && r.OwnerLogin == login)))
                && !db.Restaurants.Any(r => r.Id == restaurant && r.DisabledAt != null), ct);
    }

    /// <summary>Токен несёт логин, роль и ресторан: по ресторану сервер отбирает данные для каждого запроса.</summary>
    /// <param name="restaurant">Ресторан, в который входит владелец; по умолчанию — тот, где заведена учётная запись.</param>
    public static LoginResponse Issue(User user, AuthOptions options, TimeProvider time, Guid? restaurant = null)
    {
        var now = time.GetUtcNow();
        var expiresAt = now.AddHours(options.TokenHours);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AuthOptions.Issuer,
            Audience = AuthOptions.Issuer,
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, user.Login),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(RestaurantClaim, (restaurant ?? user.RestaurantId).ToString()),
                new Claim(VersionClaim, user.TokenVersion.ToString()),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return new LoginResponse(token, expiresAt);
    }

    private static IResult Locked(int minutes) =>
        Results.Problem("Слишком много неверных попыток. Вход временно закрыт.", statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?> { ["retryAfterMinutes"] = minutes });

    /// <summary>
    /// Создаёт демо-ресторан и его учётные записи из настроек, которых ещё нет, и поддерживает их должность и роль.
    /// Вызывается при запуске, когда текущим рестораном выбран демо-ресторан.
    /// </summary>
    public static async Task EnsureUsersAsync(AppDbContext db, AuthOptions options, TimeProvider time, CancellationToken ct = default)
    {
        if (!await db.Restaurants.AnyAsync(r => r.Id == Restaurant.DemoId, ct))
            db.Restaurants.Add(new Restaurant { Id = Restaurant.DemoId, Name = "Демо-ресторан", CreatedAt = time.GetUtcNow() });

        var existing = await db.Users.ToDictionaryAsync(u => u.Login, ct);
        var hasher = new PasswordHasher<User>();
        foreach (var demo in options.Users.Where(u => u.Login != ""))
        {
            if (!existing.TryGetValue(demo.Login, out var user))
            {
                user = new User { Id = Guid.NewGuid(), Login = demo.Login, PasswordHash = "" };
                user.PasswordHash = hasher.HashPassword(user, options.DemoPassword);
                db.Users.Add(user);
            }
            (user.Title, user.Role) = (demo.Title, demo.Role);
        }

        if (options.SupportPassword != "")
        {
            if (!existing.TryGetValue(options.SupportLogin, out var support))
            {
                support = new User { Id = Guid.NewGuid(), Login = options.SupportLogin, PasswordHash = "" };
                db.Users.Add(support);
            }
            (support.Title, support.Role) = ("Поддержка", Role.Manager);
            if (support.PasswordHash == "" || hasher.VerifyHashedPassword(support, support.PasswordHash, options.SupportPassword) == PasswordVerificationResult.Failed)
            {
                // Пароль поддержки сменили (обычно — после утечки): выданные по старому паролю токены и подключённые
                // им чаты Telegram больше не действуют, операторы подключаются заново новым паролем.
                if (support.PasswordHash != "")
                {
                    support.TokenVersion++;
                    db.TelegramOperators.RemoveRange(await db.TelegramOperators.ToListAsync(ct));
                }
                support.PasswordHash = hasher.HashPassword(support, options.SupportPassword);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
