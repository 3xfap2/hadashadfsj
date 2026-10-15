using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="ToAll">Сообщение владельца всем сотрудникам ресторана.</param>
public sealed record StaffMessageRow(long Id, DateTimeOffset At, bool FromOwner, string Author, bool ToAll, string Text);

/// <summary>Диалог в кабинете владельца: ресторан и сотрудник; Employee пуст у рассылки «всем сотрудникам».</summary>
/// <param name="Unread">Сколько сообщений сотрудника владелец ещё не прочитал.</param>
public sealed record OwnerDialog(Guid RestaurantId, string Restaurant, string? Employee, DateTimeOffset LastAt, string LastText, bool LastFromOwner, int Unread);

/// <param name="Employee">Логин сотрудника; пусто — всем сотрудникам ресторана.</param>
public sealed record OwnerMessageRequest(Guid RestaurantId, string? Employee, string? Text);
public sealed record MessageTextRequest(string? Text);
public sealed record MyMessages(int Unread, IEnumerable<StaffMessageRow> Messages);
public sealed record UnreadCount(int Unread);

/// <summary>
/// Переписка владельца с сотрудниками его ресторанов. Владелец пишет из своего кабинета одному сотруднику или всем
/// сотрудникам ресторана; сотрудник видит только свой диалог и сообщения «всем» своего ресторана и отвечает в кабинете ресторана.
/// </summary>
public static class OwnerMessages
{
    public const int MaxText = 2000;
    /// <summary>Сколько последних сообщений отдаёт диалог.</summary>
    private const int Page = 200;

    public static void MapOwnerMessages(this RouteGroupBuilder api)
    {
        // ——— Сторона владельца ———
        var owner = api.MapGroup("/owner/messages").WithTags("Кабинет владельца").RequireRoles(Access.Staff).AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            return await OwnerCabinet.IsOwnerAsync(http.RequestServices.GetRequiredService<AppDbContext>(), http.User.Identity?.Name, http.RequestAborted)
                ? await next(context)
                : Results.Problem(OwnerCabinet.NotOwner, statusCode: StatusCodes.Status403Forbidden);
        });

        owner.MapGet("", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var owned = (await OwnerCabinet.OwnedListAsync(db, principal, ct)).ToDictionary(r => r.Id, r => r.Name);
            var ids = owned.Keys.ToList();
            var dialogs = await db.StaffMessages.IgnoreQueryFilters()
                .Where(m => ids.Contains(m.RestaurantId))
                .GroupBy(m => new { m.RestaurantId, m.Employee })
                .Select(g => new { g.Key.RestaurantId, g.Key.Employee, LastId = g.Max(m => m.Id), Unread = g.Count(m => !m.FromOwner && m.ReadAt == null) })
                .ToListAsync(ct);
            // Текст хранится зашифрованным, поэтому последние сообщения читаются отдельно, по номерам.
            var lastIds = dialogs.Select(d => d.LastId).ToList();
            var last = await db.StaffMessages.IgnoreQueryFilters().AsNoTracking().Where(m => lastIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
            return dialogs
                .Select(d => new OwnerDialog(d.RestaurantId, owned[d.RestaurantId], d.Employee, last[d.LastId].At, last[d.LastId].Text, last[d.LastId].FromOwner, d.Unread))
                .OrderByDescending(d => d.LastAt)
                .ToList();
        });

        owner.MapGet("{restaurantId:guid}", async (Guid restaurantId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct, string? employee = null) =>
        {
            if (!await OwnedAsync(db, principal, restaurantId, ct)) return Results.NotFound();
            employee = Normalize(employee);
            var rows = await db.StaffMessages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.RestaurantId == restaurantId && m.Employee == employee)
                .OrderByDescending(m => m.Id).Take(Page)
                .ToListAsync(ct);
            return Results.Ok(rows.OrderBy(m => m.Id).Select(Row));
        });

        // Открытый диалог прочитан: сообщения сотрудника в нём больше не считаются новыми.
        owner.MapPost("{restaurantId:guid}/read", async (Guid restaurantId, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken ct, string? employee = null) =>
        {
            if (!await OwnedAsync(db, principal, restaurantId, ct)) return Results.NotFound();
            employee = Normalize(employee);
            var now = time.GetUtcNow();
            await db.StaffMessages.IgnoreQueryFilters()
                .Where(m => m.RestaurantId == restaurantId && m.Employee == employee && !m.FromOwner && m.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), ct);
            return Results.NoContent();
        });

        owner.MapPost("", async (OwnerMessageRequest req, ClaimsPrincipal principal, AppDbContext db, Tenant tenant, TimeProvider time, CancellationToken ct) =>
        {
            if (!await OwnedAsync(db, principal, req.RestaurantId, ct)) return Results.NotFound();
            var login = principal.Identity!.Name!;
            var employee = Normalize(req.Employee);
            var text = (req.Text ?? "").Trim();
            var errors = new Errors()
                .Check(text is { Length: > 0 and <= MaxText }, "text", $"Напишите сообщение — до {MaxText} символов.")
                .Check(employee != login, "employee", "Себе написать нельзя.")
                .Check(employee is null || await db.Users.IgnoreQueryFilters().AnyAsync(u => u.RestaurantId == req.RestaurantId && u.Login == employee, ct),
                    "employee", "Такого сотрудника в этом ресторане нет.");
            if (errors.Any) return errors.ToResult();

            // Запись — от имени выбранного ресторана: событие об изменении получат его кабинеты, и сотрудник увидит сообщение сразу.
            tenant.Id = req.RestaurantId;
            var message = new StaffMessage
            {
                RestaurantId = req.RestaurantId, At = time.GetUtcNow(), FromOwner = true, Author = login, Employee = employee, Text = text,
            };
            db.StaffMessages.Add(message);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/owner/messages/{req.RestaurantId}", Row(message));
        });

        // ——— Сторона сотрудника (кабинет ресторана) ———
        var mine = api.MapGroup("/messages").WithTags("Сообщения владельца").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            return await CanWriteToOwnerAsync(db, http.User.Identity?.Name, http.RequestAborted)
                ? await next(context)
                : Results.Problem("Сообщений владельца здесь нет: у ресторана нет владельца в «Остатке», а сам владелец переписывается из кабинета владельца.",
                    statusCode: StatusCodes.Status403Forbidden);
        });

        mine.MapGet("", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var login = principal.Identity!.Name!;
            var lastRead = await db.Users.Where(u => u.Login == login).Select(u => u.LastReadMessageId).FirstAsync(ct);
            var rows = await Visible(db, login).AsNoTracking().OrderByDescending(m => m.Id).Take(Page).ToListAsync(ct);
            return new MyMessages(await Unread(db, login, lastRead).CountAsync(ct), rows.OrderBy(m => m.Id).Select(Row));
        });

        mine.MapGet("/unread", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var login = principal.Identity!.Name!;
            var lastRead = await db.Users.Where(u => u.Login == login).Select(u => u.LastReadMessageId).FirstAsync(ct);
            return new UnreadCount(await Unread(db, login, lastRead).CountAsync(ct));
        });

        mine.MapPost("/read", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var login = principal.Identity!.Name!;
            var user = await db.Users.FirstAsync(u => u.Login == login, ct);
            var last = await Visible(db, login).MaxAsync(m => (long?)m.Id, ct) ?? 0;
            // Отметка только растёт: старая вкладка не вернёт прочитанному статус нового.
            if (last > user.LastReadMessageId)
            {
                user.LastReadMessageId = last;
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        mine.MapPost("", async (MessageTextRequest req, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var text = (req.Text ?? "").Trim();
            var errors = new Errors().Check(text is { Length: > 0 and <= MaxText }, "text", $"Напишите сообщение — до {MaxText} символов.");
            if (errors.Any) return errors.ToResult();
            var login = principal.Identity!.Name!;
            var message = new StaffMessage { At = time.GetUtcNow(), FromOwner = false, Author = login, Employee = login, Text = text };
            db.StaffMessages.Add(message);
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/messages", Row(message));
        });
    }

    /// <summary>
    /// Сотрудник может переписываться с владельцем, если у ресторана есть владелец и это не он сам:
    /// владелец пишет из своего кабинета. Ресторан — текущий, из токена.
    /// </summary>
    public static Task<bool> CanWriteToOwnerAsync(AppDbContext db, string? login, CancellationToken ct)
    {
        var restaurant = db.TenantId;
        return db.Restaurants.AnyAsync(r => r.Id == restaurant && r.OwnerLogin != null && r.OwnerLogin != login, ct);
    }

    /// <summary>Что видит сотрудник: свой диалог и сообщения владельца всем сотрудникам. Ресторан — из фильтра.</summary>
    private static IQueryable<StaffMessage> Visible(AppDbContext db, string login) =>
        db.StaffMessages.Where(m => m.Employee == login || m.Employee == null);

    private static IQueryable<StaffMessage> Unread(AppDbContext db, string login, long lastRead) =>
        Visible(db, login).Where(m => m.FromOwner && m.Id > lastRead);

    private static Task<bool> OwnedAsync(AppDbContext db, ClaimsPrincipal principal, Guid restaurantId, CancellationToken ct) =>
        OwnerEndpoints.OwnerOf(db, principal).AnyAsync(r => r.Id == restaurantId, ct);

    private static string? Normalize(string? employee) =>
        string.IsNullOrWhiteSpace(employee) ? null : employee.Trim().ToLowerInvariant();

    private static StaffMessageRow Row(StaffMessage m) => new(m.Id, m.At, m.FromOwner, m.Author, m.Employee is null, m.Text);
}
