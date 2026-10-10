using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record StaffRequest(string? Login, string? Password, string? Title, Role Role);
/// <param name="Password">Новый пароль; пусто — пароль не меняется.</param>
public sealed record StaffUpdate(string? Title, Role Role, string? Password = null);
public sealed record StaffResponse(Guid Id, string Login, string Title, Role Role);

/// <summary>Сотрудники ресторана: управляющий заводит учётные записи и назначает роли.</summary>
public static partial class StaffEndpoints
{
    [GeneratedRegex("^[a-z0-9._-]{3,40}$")]
    public static partial Regex LoginPattern();

    public const string LoginRule = "Логин — от 3 до 40 символов: латинские буквы, цифры, точка, дефис, подчёркивание.";
    public const string PasswordRule = "Пароль — не короче 8 символов.";

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
                .Check(title is { Length: > 0 and <= 60 }, "title", "Укажите должность — до 60 символов.")
                .Check(Enum.IsDefined(req.Role), "role", "Неизвестная роль.");
            if (errors.Any) return errors.ToResult();
            // Логин один на всю систему: по нему при входе определяется ресторан.
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Login == login, ct))
                return Results.Problem("Такой логин уже занят.", statusCode: StatusCodes.Status409Conflict);

            var user = new User { Id = Guid.NewGuid(), Login = login, PasswordHash = "", Title = title, Role = req.Role };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, req.Password!);
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
                .Check(Enum.IsDefined(req.Role), "role", "Неизвестная роль.")
                .Check(string.IsNullOrEmpty(req.Password) || req.Password.Length is >= 8 and <= 100, "password", PasswordRule);
            if (errors.Any) return errors.ToResult();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (IsDemoAccount(user, options)) return DemoAccount();
            // Управляющий не может снять роль с себя: иначе в ресторане не останется того, кто управляет сотрудниками.
            if (user.Login == principal.Identity?.Name && req.Role != user.Role)
                return Results.Problem("Свою роль изменить нельзя.", statusCode: StatusCodes.Status409Conflict);

            // Смена роли или пароля обнуляет выданные токены: сотруднику придётся войти заново.
            if (req.Role != user.Role || !string.IsNullOrEmpty(req.Password)) user.TokenVersion++;
            (user.Title, user.Role) = (title, req.Role);
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
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>Открытые учётные записи демо-ресторана: по ним систему смотрят без регистрации, поэтому менять их нельзя.</summary>
    private static bool IsDemoAccount(User user, AuthOptions options) =>
        user.RestaurantId == Restaurant.DemoId && options.Users.Any(u => u.Login == user.Login);

    private static IResult DemoAccount() =>
        Results.Problem("Это открытая учётная запись демо-ресторана, изменить или удалить её нельзя. Заведите нового сотрудника.", statusCode: StatusCodes.Status409Conflict);
}
