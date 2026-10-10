using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record StaffRequest(string? Login, string? Password, string? Title, Role Role);
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

        staff.MapDelete("{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (user.Login == principal.Identity?.Name)
                return Results.Problem("Свою учётную запись удалить нельзя.", statusCode: StatusCodes.Status409Conflict);
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
