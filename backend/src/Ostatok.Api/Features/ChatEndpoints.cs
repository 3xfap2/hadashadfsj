using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record ChatStartRequest(string? Name, string? Contact, string? Text);
public sealed record ChatTextRequest(string? Text);
public sealed record ChatMessageResponse(long Id, DateTimeOffset At, bool FromStaff, string Text);
public sealed record ChatResponse(Guid Id, List<ChatMessageResponse> Messages);
public sealed record ChatRow(Guid Id, string Name, string Contact, DateTimeOffset LastMessageAt, int Unread, string LastText, bool LastFromStaff);
public sealed record ChatDetails(Guid Id, string Name, string Contact, DateTimeOffset CreatedAt, List<ChatMessageResponse> Messages);

/// <summary>
/// Вопросы посетителей лендинга. Посетитель пишет без входа, отвечает сотрудник из кабинета
/// (или бот — по тем же адресам /api/support).
/// </summary>
public static class ChatEndpoints
{
    public const string RateLimit = "chat";
    private const int MaxText = 1000;

    public static void MapChats(this IEndpointRouteBuilder app)
    {
        // Адреса посетителя открыты. Ключ к диалогу — его случайный идентификатор: чужой диалог не подобрать.
        var visitor = app.MapGroup("/api/chats").WithTags("Чат на лендинге").AllowAnonymous().RequireRateLimiting(RateLimit);

        visitor.MapPost("", async (ChatStartRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var (name, contact, text) = (req.Name?.Trim() ?? "", req.Contact?.Trim() ?? "", req.Text?.Trim() ?? "");
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "name", "Как к вам обращаться?")
                .Check(contact.Length <= 120 && LeadEndpoints.IsContact(contact), "contact", "Укажите телефон или почту.")
                .Check(text is { Length: > 0 and <= MaxText }, "text", $"Напишите вопрос — до {MaxText} символов.");
            if (errors.Any) return errors.ToResult();

            var now = time.GetUtcNow();
            var chat = new Chat
            {
                Id = Guid.NewGuid(), CreatedAt = now, LastMessageAt = now, Name = name, Contact = contact, Unread = 1,
                Messages = [new ChatMessage { At = now, Text = text }],
            };
            db.Chats.Add(chat);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/chats/{chat.Id}", new ChatResponse(chat.Id, chat.Messages.Select(ToResponse).ToList()));
        });

        visitor.MapGet("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Chats.AnyAsync(c => c.Id == id, ct)
                ? Results.Ok(new ChatResponse(id, await MessagesAsync(db, id, ct)))
                : Results.NotFound());

        visitor.MapPost("{id:guid}/messages", (Guid id, ChatTextRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
            AddMessageAsync(id, req, staff: null, db, time, ct));

        var support = app.MapGroup("/api/support/chats").WithTags("Обращения").RequireRoles(Access.Leads);

        support.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            await db.Chats
                .OrderByDescending(c => c.LastMessageAt)
                .Take(100)
                .Select(c => new { c.Id, c.Name, c.Contact, c.LastMessageAt, c.Unread, Last = c.Messages.OrderByDescending(m => m.Id).First() })
                .Select(c => new ChatRow(c.Id, c.Name, c.Contact, c.LastMessageAt, c.Unread, c.Last.Text, c.Last.FromStaff))
                .ToListAsync(ct));

        support.MapGet("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct) is { } chat
                ? Results.Ok(new ChatDetails(chat.Id, chat.Name, chat.Contact, chat.CreatedAt, await MessagesAsync(db, id, ct)))
                : Results.NotFound());

        support.MapPost("{id:guid}/read", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            await db.Chats.Where(c => c.Id == id && c.Unread > 0).ExecuteUpdateAsync(s => s.SetProperty(c => c.Unread, 0), ct);
            return Results.NoContent();
        });

        support.MapPost("{id:guid}/messages", (Guid id, ChatTextRequest req, ClaimsPrincipal user, AppDbContext db, TimeProvider time, CancellationToken ct) =>
            AddMessageAsync(id, req, staff: user.Identity?.Name ?? "", db, time, ct));
    }

    /// <summary>Добавляет сообщение в диалог; staff — логин сотрудника, null — пишет посетитель.</summary>
    private static async Task<IResult> AddMessageAsync(Guid id, ChatTextRequest req, string? staff, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var text = req.Text?.Trim() ?? "";
        var errors = new Errors().Check(text is { Length: > 0 and <= MaxText }, "text", $"Напишите сообщение — до {MaxText} символов.");
        if (errors.Any) return errors.ToResult();

        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return Results.NotFound();

        var message = new ChatMessage { ChatId = id, At = time.GetUtcNow(), FromStaff = staff is not null, Author = staff ?? "", Text = text };
        db.ChatMessages.Add(message);
        chat.LastMessageAt = message.At;
        // Ответ сотрудника означает, что диалог прочитан; сообщение посетителя ждёт ответа.
        chat.Unread = staff is null ? chat.Unread + 1 : 0;
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/chats/{id}", ToResponse(message));
    }

    private static async Task<List<ChatMessageResponse>> MessagesAsync(AppDbContext db, Guid chatId, CancellationToken ct) =>
        (await db.ChatMessages.Where(m => m.ChatId == chatId).OrderBy(m => m.Id).ToListAsync(ct)).Select(ToResponse).ToList();

    private static ChatMessageResponse ToResponse(ChatMessage m) => new(m.Id, m.At, m.FromStaff, m.Text);
}
