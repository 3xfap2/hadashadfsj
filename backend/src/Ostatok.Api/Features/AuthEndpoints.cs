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
public sealed record MeResponse(string Login, string Title, Role Role, IEnumerable<string> Sections, ShiftInfo Shift, IEnumerable<string> Places);

public sealed class RestaurantOptions
{
    public string ShiftStart { get; set; } = "12:00";
    public string ShiftEnd { get; set; } = "24:00";
    /// <summary>Места, откуда приходят заказы: столы и бар.</summary>
    public string[] Places { get; set; } = [];
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

            var user = await db.Users.FirstOrDefaultAsync(u => u.Login == req.Login, ct);
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
            var expiresAt = time.GetUtcNow().AddHours(options.TokenHours);
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = AuthOptions.Issuer,
                Audience = AuthOptions.Issuer,
                Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Login), new Claim(ClaimTypes.Role, user.Role.ToString())]),
                Expires = expiresAt.UtcDateTime,
                SigningCredentials = new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256),
            });
            return Results.Ok(new LoginResponse(token, expiresAt));
        })
        .WithTags("Вход")
        .AllowAnonymous()
        .RequireRateLimiting(LoginRateLimit);

        app.MapGet("/api/me", async (ClaimsPrincipal principal, AppDbContext db, RestaurantOptions restaurant, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Login == principal.Identity!.Name, ct);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new MeResponse(user.Login, user.Title, user.Role, Access.Sections(user.Role),
                    new ShiftInfo(restaurant.ShiftStart, restaurant.ShiftEnd), restaurant.Places));
        })
        .WithTags("Вход")
        .RequireAuthorization();
    }

    private static IResult Locked(int minutes) =>
        Results.Problem("Слишком много неверных попыток. Вход временно закрыт.", statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?> { ["retryAfterMinutes"] = minutes });

    /// <summary>Создаёт учётные записи из настроек, которых ещё нет, и поддерживает их должность и роль.</summary>
    public static async Task EnsureUsersAsync(AppDbContext db, AuthOptions options, CancellationToken ct = default)
    {
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
