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
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 10;

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
    string Login, string Title, Role Role, IEnumerable<string> Sections, ShiftInfo Shift, IEnumerable<string> Places, string Restaurant);

/// <param name="Restaurant">Название нового ресторана; его первый пользователь становится управляющим.</param>
public sealed record RegisterRequest(string? Restaurant, string? Login, string? Password);

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
/// </summary>
public sealed class LoginAttempts(AuthOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (int Failed, DateTimeOffset LockedUntil)> _state = new();

    private static string Key(string? login) => (login ?? "").Trim().ToLowerInvariant();

    /// <summary>Сколько минут осталось до разблокировки; 0 — вход разрешён.</summary>
    public int LockedMinutes(string? login)
    {
        if (!_state.TryGetValue(Key(login), out var s)) return 0;
        var left = s.LockedUntil - time.GetUtcNow();
        return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalMinutes) : 0;
    }

    /// <summary>Учитывает неверную попытку и возвращает, сколько попыток осталось.</summary>
    public int Fail(string? login)
    {
        var now = time.GetUtcNow();
        var state = _state.AddOrUpdate(Key(login), _ => (1, default), (_, s) =>
        {
            // Блокировка истекла — счёт начинается заново.
            var failed = s.LockedUntil != default && s.LockedUntil <= now ? 1 : s.Failed + 1;
            return (failed, failed >= options.MaxFailedLogins ? now.AddMinutes(options.LockoutMinutes) : default);
        });
        return Math.Max(0, options.MaxFailedLogins - state.Failed);
    }

    public void Reset(string? login) => _state.TryRemove(Key(login), out _);
}

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, AuthOptions options, LoginAttempts attempts, TimeProvider time, CancellationToken ct) =>
        {
            if (attempts.LockedMinutes(req.Login) is > 0 and var minutes)
                return Locked(minutes);

            // До входа ресторан неизвестен: учётная запись ищется по логину среди всех.
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Login == req.Login, ct);
            var hasher = new PasswordHasher<User>();
            if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") == PasswordVerificationResult.Failed)
            {
                var left = attempts.Fail(req.Login);
                return left == 0
                    ? Locked(options.LockoutMinutes)
                    : Results.Problem("Неверный логин или пароль.", statusCode: StatusCodes.Status401Unauthorized,
                        extensions: new Dictionary<string, object?> { ["attemptsLeft"] = left });
            }

            attempts.Reset(req.Login);
            return Results.Ok(Issue(user, options, time));
        })
        .WithTags("Вход")
        .AllowAnonymous()
        .RequireRateLimiting(LoginRateLimit);

        // Новый ресторан — отдельное рабочее пространство: свой склад, меню и сотрудники.
        app.MapPost("/api/auth/register", async (RegisterRequest req, AppDbContext db, AuthOptions options, RestaurantOptions defaults, TimeProvider time, CancellationToken ct) =>
        {
            var (name, login) = ((req.Restaurant ?? "").Trim(), (req.Login ?? "").Trim().ToLowerInvariant());
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "restaurant", "Укажите название ресторана — до 120 символов.")
                .Check(StaffEndpoints.LoginPattern().IsMatch(login), "login", StaffEndpoints.LoginRule)
                .Check((req.Password ?? "").Length is >= 8 and <= 100, "password", StaffEndpoints.PasswordRule);
            if (errors.Any) return errors.ToResult();
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Login == login, ct))
                return Results.Problem("Такой логин уже занят.", statusCode: StatusCodes.Status409Conflict);

            var restaurant = new Restaurant
            {
                Id = Guid.NewGuid(), Name = name, CreatedAt = time.GetUtcNow(),
                ShiftStart = defaults.ShiftStart, ShiftEnd = defaults.ShiftEnd,
            };
            var user = new User
            {
                Id = Guid.NewGuid(), RestaurantId = restaurant.Id, Login = login, PasswordHash = "", Title = "Управляющий", Role = Role.Manager,
            };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, req.Password!);
            db.Restaurants.Add(restaurant);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/me", Issue(user, options, time));
        })
        .WithTags("Вход")
        .AllowAnonymous()
        .RequireRateLimiting(LeadEndpoints.RateLimit);

        app.MapGet("/api/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Login == principal.Identity!.Name, ct);
            if (user is null) return Results.Unauthorized();
            var restaurant = await db.CurrentRestaurantAsync(ct);
            return Results.Ok(new MeResponse(
                user.Login, user.Title, user.Role, Access.Sections(user.Role, user.RestaurantId == Restaurant.DemoId),
                new ShiftInfo(restaurant.ShiftStart, restaurant.ShiftEnd), restaurant.Places(), restaurant.Name));
        })
        .WithTags("Вход")
        .RequireAuthorization();
    }

    public const string RestaurantClaim = "restaurant";
    public const string VersionClaim = "ver";

    /// <summary>
    /// Токен действует, пока учётная запись существует и не менялась: после удаления сотрудника,
    /// смены роли или пароля старый токен перестаёт работать сразу, а не через часы.
    /// </summary>
    public static Task<bool> IsCurrentAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var login = principal.Identity?.Name;
        if (!Guid.TryParse(principal.FindFirst(RestaurantClaim)?.Value, out var restaurant)
            || !int.TryParse(principal.FindFirst(VersionClaim)?.Value, out var version)) return Task.FromResult(false);
        return db.Users.IgnoreQueryFilters().AnyAsync(u => u.Login == login && u.RestaurantId == restaurant && u.TokenVersion == version, ct);
    }

    /// <summary>Токен несёт логин, роль и ресторан: по ресторану сервер отбирает данные для каждого запроса.</summary>
    private static LoginResponse Issue(User user, AuthOptions options, TimeProvider time)
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
                new Claim(RestaurantClaim, user.RestaurantId.ToString()),
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
        await db.SaveChangesAsync(ct);
    }
}
