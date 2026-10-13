using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record StaffRequest(string? Login, string? Password, string? Title, Role? Role);
/// <param name="Password">Новый пароль; пусто — пароль не меняется.</param>
public sealed record StaffUpdate(string? Title, Role? Role, string? Password = null);
public sealed record StaffResponse(Guid Id, string Login, string Title, Role Role);

/// <summary>Сотрудники ресторана: управляющий заводит учётные записи и назначает роли.</summary>
public static partial class StaffEndpoints
{
    [GeneratedRegex("^[a-z0-9._-]{3,40}$")]
    public static partial Regex LoginPattern();

    public const string LoginRule = "Логин — от 3 до 40 символов: латинские буквы, цифры, точка, дефис, подчёркивание.";
    public const string PasswordRule = "Пароль — не короче 8 символов.";
    public const string WeakPassword = "Пароль слишком простой: его подберут первым. Не используйте логин, повторы и пароли вроде «12345678».";

    /// <summary>Самые частые пароли от 8 символов — их перебирают первыми.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "12345678", "123456789", "1234567890", "87654321", "password", "password1", "qwerty123", "qwertyuiop",
        "1q2w3e4r", "1q2w3e4r5t", "iloveyou", "йцукенгш", "пароль123", "qwerty12", "abc12345", "demo2026",
    };

    /// <summary>Пароль совпадает с логином, состоит из одного-двух повторяющихся знаков или есть в списке частых.</summary>
    public static bool IsWeak(string password, string login) =>
        password.Equals(login, StringComparison.OrdinalIgnoreCase) || password.Distinct().Count() <= 2 || Common.Contains(password);

    public static void MapStaff(this RouteGroupBuilder api)
    {
        var staff = api.MapGroup("/users").WithTags("Сотрудники").RequireRoles(Access.Staff);

        staff.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            await db.Users.OrderBy(u => u.Login).Select(u => new StaffResponse(u.Id, u.Login, u.Title, u.Role)).ToListAsync(ct));

        staff.MapPost("", async (StaffRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var (login, title) = ((req.Login ?? "").Trim().ToLowerInvariant(), (req.Title ?? "").Trim());
            var errors = new Errors()
                .Check(LoginPattern().IsMatch(login), "login", LoginRule)
                .Check((req.Password ?? "").Length is >= 8 and <= 100, "password", PasswordRule)
                .Check(!IsWeak(req.Password ?? "", login), "password", WeakPassword)
                .Check(title is { Length: > 0 and <= 60 }, "title", "Укажите должность — до 60 символов.")
                // Без явной роли перечисление получило бы первое значение — управляющего.
                .Check(req.Role is { } role && Enum.IsDefined(role), "role", "Укажите роль.");
            if (errors.Any) return errors.ToResult();
            // Логин один на всю систему: по нему при входе определяется ресторан.
            if (await LoginTakenAsync(db, login, ct))
                return Results.Problem("Такой логин уже занят.", statusCode: StatusCodes.Status409Conflict);

            var user = new User { Id = Guid.NewGuid(), Login = login, PasswordHash = "", Title = title, Role = req.Role!.Value };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, req.Password!);
            // Сигналы, которые были до прихода сотрудника, для него не новые.
            user.LastReadAlertId = await db.Alerts.MaxAsync(a => (long?)a.Id, ct) ?? 0;
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/users/{user.Id}", new StaffResponse(user.Id, user.Login, user.Title, user.Role));
        });

        // Смена роли или должности и сброс пароля, если сотрудник его забыл.
        staff.MapPut("{id:guid}", async (Guid id, StaffUpdate req, ClaimsPrincipal principal, AppDbContext db, AuthOptions options, CancellationToken ct) =>
        {
            var title = (req.Title ?? "").Trim();
            var errors = new Errors()
                .Check(title is { Length: > 0 and <= 60 }, "title", "Укажите должность — до 60 символов.")
                .Check(req.Role is { } role && Enum.IsDefined(role), "role", "Укажите роль.")
                .Check(string.IsNullOrEmpty(req.Password) || req.Password.Length is >= 8 and <= 100, "password", PasswordRule);
            if (errors.Any) return errors.ToResult();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (!string.IsNullOrEmpty(req.Password) && IsWeak(req.Password, user.Login)) return errors.Add("password", WeakPassword).ToResult();
            if (IsDemoAccount(user, options)) return DemoAccount();
            // Управляющий не может снять роль с себя: иначе в ресторане не останется того, кто управляет сотрудниками.
            if (user.Login == principal.Identity?.Name && req.Role != user.Role)
                return Results.Problem("Свою роль изменить нельзя.", statusCode: StatusCodes.Status409Conflict);
            // С паролем владельца другой управляющий вошёл бы во все рестораны, которые тот добавил себе сам.
            if (user.Login != principal.Identity?.Name && (req.Role != user.Role || !string.IsNullOrEmpty(req.Password)) && await OwnsRestaurantsAsync(db, user, ct))
                return OwnerAccount();

            // Смена роли или пароля обнуляет выданные токены: сотруднику придётся войти заново.
            if (req.Role != user.Role || !string.IsNullOrEmpty(req.Password)) user.TokenVersion++;
            (user.Title, user.Role) = (title, req.Role!.Value);
            if (!string.IsNullOrEmpty(req.Password)) user.PasswordHash = new PasswordHasher<User>().HashPassword(user, req.Password);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new StaffResponse(user.Id, user.Login, user.Title, user.Role));
        });

        staff.MapDelete("{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, AuthOptions options, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (IsDemoAccount(user, options)) return DemoAccount();
            if (user.Login == principal.Identity?.Name)
                return Results.Problem("Свою учётную запись удалить нельзя.", statusCode: StatusCodes.Status409Conflict);
            // Рестораны владельца привязаны к его логину: освободившийся логин мог бы занять кто угодно и получить их.
            if (await OwnsRestaurantsAsync(db, user, ct)) return OwnerAccount();
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>Открытые учётные записи демо-ресторана: по ним систему смотрят без регистрации, поэтому менять их нельзя.</summary>
    public static bool IsDemoAccount(User user, AuthOptions options) =>
        user.RestaurantId == Restaurant.DemoId && (options.Users.Any(u => u.Login == user.Login) || user.Login == options.SupportLogin);

    /// <summary>Логин занят сотрудником или числится владельцем ресторана (запись владельца могли удалить до того, как это запретили).</summary>
    public static async Task<bool> LoginTakenAsync(AppDbContext db, string login, CancellationToken ct) =>
        await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Login == login, ct) || await db.Restaurants.AnyAsync(r => r.OwnerLogin == login, ct);

    private static Task<bool> OwnsRestaurantsAsync(AppDbContext db, User user, CancellationToken ct) =>
        db.Restaurants.AnyAsync(r => r.OwnerLogin == user.Login, ct);

    private static IResult OwnerAccount() =>
        Results.Problem("Это владелец ресторанов, которые он добавил себе сам: пароль и роль он меняет только сам, удалить запись нельзя. Сброс пароля — через поддержку.", statusCode: StatusCodes.Status409Conflict);

    private static IResult DemoAccount() =>
        Results.Problem("Это открытая учётная запись демо-ресторана, изменить или удалить её нельзя. Заведите нового сотрудника.", statusCode: StatusCodes.Status409Conflict);
}
