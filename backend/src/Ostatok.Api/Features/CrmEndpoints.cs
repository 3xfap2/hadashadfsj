using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record CrmLeadCounts(int New, int Contacted, int Demo, int Won, int Lost);
public sealed record CrmSummary(CrmLeadCounts Leads, int UnreadChats, int TasksOverdue, int TasksToday, int Restaurants, int RestaurantsActive7d);

public sealed record CrmNoteResponse(long Id, DateTimeOffset CreatedAt, string Author, string Text, long? LeadId, Guid? RestaurantId);
public sealed record CrmTaskResponse(
    long Id, DateTimeOffset CreatedAt, string Title, DateTimeOffset DueAt, DateTimeOffset? DoneAt,
    long? LeadId, string? LeadName, Guid? RestaurantId, string? RestaurantName);

/// <param name="Notes">Сколько заметок у заявки.</param>
/// <param name="OpenTasks">Сколько у неё невыполненных задач.</param>
public record CrmLeadRow(
    long Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Name, string Organization, string Contact, string Points, string? Comment,
    LeadStatus Status, string? Assignee, Guid? RestaurantId, string? RestaurantName, Guid? ChatId, int Notes, int OpenTasks);

/// <summary>Карточка заявки: те же поля, что в строке списка, плюс сами заметки и задачи.</summary>
public sealed record CrmLeadCard : CrmLeadRow
{
    public CrmLeadCard(CrmLeadRow row, List<CrmNoteResponse> notesList, List<CrmTaskResponse> tasks) : base(row) =>
        (NotesList, Tasks) = (notesList, tasks);

    public List<CrmNoteResponse> NotesList { get; }
    public List<CrmTaskResponse> Tasks { get; }
}

/// <summary>Ресторан глазами команды продукта: только счётчики, без содержимого склада.</summary>
/// <param name="LastActivityAt">Последняя запись в журнале движений: поставка, заказ, списание, заготовка или инвентаризация.</param>
public record CrmRestaurantRow(
    Guid Id, string Name, DateTimeOffset CreatedAt, bool IsDemo, DateTimeOffset? DisabledAt, int Staff, string? ManagerLogin,
    int Products, int OrdersLast7Days, DateTimeOffset? LastActivityAt, long? LeadId, int Notes, int OpenTasks);

public sealed record CrmUser(string Login, string Title, Role Role);

public sealed record CrmRestaurantCard : CrmRestaurantRow
{
    public CrmRestaurantCard(CrmRestaurantRow row, List<CrmUser> users, List<CrmNoteResponse> notesList, List<CrmTaskResponse> tasks,
        string? contactName = null, string? contact = null) : base(row) =>
        (Users, NotesList, Tasks, ContactName, Contact) = (users, notesList, tasks, contactName, contact);

    /// <summary>Кто оставил заявку, по которой открыт кабинет, и как с ним связаться; пусто, если кабинет создан без заявки.</summary>
    public string? ContactName { get; }
    public string? Contact { get; }

    public List<CrmUser> Users { get; }
    public List<CrmNoteResponse> NotesList { get; }
    public List<CrmTaskResponse> Tasks { get; }
}

public sealed record CrmChatRow(
    Guid Id, string Name, string Contact, DateTimeOffset LastMessageAt, int Unread, string LastText, bool LastFromStaff, long? LeadId);

public sealed record CrmLeadUpdate(LeadStatus? Status, string? Assignee);
public sealed record CrmAccess(string Name, string Login, string Password);
public sealed record CrmResetRequest(string? Login);
public sealed record CrmPassword(string Login, string Password);
public sealed record CrmChatLead(long? LeadId);
public sealed record CrmNoteRequest(long? LeadId, Guid? RestaurantId, string? Text);
public sealed record CrmTaskRequest(string? Title, DateTimeOffset? DueAt, long? LeadId, Guid? RestaurantId);
/// <param name="Done">Не указано — отметка «сделано» остаётся как была.</param>
public sealed record CrmTaskUpdate(string? Title, DateTimeOffset? DueAt, bool? Done);

/// <summary>
/// CRM команды продукта: воронка заявок, рестораны-клиенты, диалоги лендинга, заметки и задачи.
/// Работает от имени закрытой записи поддержки в демо-ресторане, поэтому каждое сохранение само сообщает
/// открытой CRM «данные изменились» (см. <see cref="PublishChanges"/>).
/// О чужих ресторанах CRM знает только счётчики и список сотрудников: содержимое склада она не читает.
/// </summary>
public static class CrmEndpoints
{
    private const int MaxNote = 2000;

    public static void MapCrm(this RouteGroupBuilder api)
    {
        var crm = api.MapGroup("/crm").WithTags("CRM").RequireRoles(Access.Leads).VendorOnly();

        crm.MapGet("/summary", async (AppDbContext db, StockOptions options, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var zone = TimeSpan.FromHours(options.UtcOffsetHours);
            var dayEnd = new DateTimeOffset(now.ToOffset(zone).Date.AddDays(1), zone).ToUniversalTime();
            var since = now.AddDays(-7);
            var leads = await db.Leads.GroupBy(l => l.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            int Leads(LeadStatus status) => leads.GetValueOrDefault(status);
            var open = db.CrmTasks.Where(t => t.DoneAt == null);
            return new CrmSummary(
                new CrmLeadCounts(Leads(LeadStatus.New), Leads(LeadStatus.Contacted), Leads(LeadStatus.Demo), Leads(LeadStatus.Won), Leads(LeadStatus.Lost)),
                await db.Chats.CountAsync(c => c.Unread > 0, ct),
                // Просрочена задача, срок которой уже прошёл; «на сегодня» — те, чей срок ещё впереди, но до конца дня.
                await open.CountAsync(t => t.DueAt < now, ct),
                await open.CountAsync(t => t.DueAt >= now && t.DueAt < dayEnd, ct),
                await db.Restaurants.CountAsync(r => r.Id != Restaurant.DemoId, ct),
                // Автосписание делает сам сервер: оно не значит, что рестораном кто-то пользуется.
                await db.Movements.IgnoreQueryFilters().Where(m => m.At >= since && m.RestaurantId != Restaurant.DemoId && m.Actor != WriteOffService.System)
                    .Select(m => m.RestaurantId).Distinct().CountAsync(ct));
        });

        crm.MapGet("/leads", async (LeadStatus? status, AppDbContext db, CancellationToken ct) =>
            await LeadRows(db, db.Leads.Where(l => status == null || l.Status == status).OrderByDescending(l => l.Id).Take(500)).ToListAsync(ct));

        crm.MapGet("/leads/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
            await LeadRows(db, db.Leads.Where(l => l.Id == id)).FirstOrDefaultAsync(ct) is { } row
                ? Results.Ok(new CrmLeadCard(row, await NotesAsync(db.CrmNotes.Where(n => n.LeadId == id), ct),
                    await TaskRows(db, db.CrmTasks.Where(t => t.LeadId == id).OrderBy(t => t.DueAt)).ToListAsync(ct)))
                : Results.NotFound());

        crm.MapPut("/leads/{id:long}", async (long id, CrmLeadUpdate req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var assignee = (req.Assignee ?? "").Trim();
            var errors = new Errors()
                .Check(req.Status is not null, "status", "Укажите статус заявки.")
                .Check(assignee.Length <= 60, "assignee", "Ответственный — до 60 символов.");
            if (errors.Any) return errors.ToResult();

            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (lead is null) return Results.NotFound();
            (lead.Status, lead.Assignee, lead.UpdatedAt) = (req.Status!.Value, assignee == "" ? null : assignee, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.Ok(await LeadRows(db, db.Leads.Where(l => l.Id == id)).FirstAsync(ct));
        });

        crm.MapPost("/leads/{id:long}/restaurant", async (long id, RestaurantCreateRequest req, AppDbContext db, RestaurantOptions defaults, TimeProvider time, CancellationToken ct) =>
        {
            // Заявка блокируется до конца операции: два одновременных нажатия не откроют два кабинета,
            // а ресторан и привязка к заявке записываются вместе.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lead = (await db.Leads.FromSql($"""SELECT * FROM "Leads" WHERE "Id" = {id} FOR UPDATE""").ToListAsync(ct)).FirstOrDefault();
            if (lead is null) return Results.NotFound();
            if (lead.RestaurantId is not null)
                return Results.Problem("По этой заявке кабинет уже открыт.", statusCode: StatusCodes.Status409Conflict);

            var created = await Restaurants.CreateAsync(db, req.Restaurant, req.Login, defaults, time, ct);
            if (created.Error is not null) return NotCreated(created.Error);
            lead.RestaurantId = await db.Users.IgnoreQueryFilters().Where(u => u.Login == created.Login).Select(u => u.RestaurantId).FirstAsync(ct);
            (lead.Status, lead.UpdatedAt) = (LeadStatus.Won, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/crm/restaurants/{lead.RestaurantId}", new CrmAccess(created.Name, created.Login, created.Password));
        });

        crm.MapGet("/restaurants", async (AppDbContext db, TimeProvider time, CancellationToken ct) =>
            await RestaurantRows(db, db.Restaurants.OrderBy(r => r.CreatedAt), time.GetUtcNow()).ToListAsync(ct));

        crm.MapGet("/restaurants/{id:guid}", async (Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (await RestaurantRows(db, db.Restaurants.Where(r => r.Id == id), time.GetUtcNow()).FirstOrDefaultAsync(ct) is not { } row) return Results.NotFound();
            // История клиента начинается с заявки: её заметки, задачи и контакт видны и в карточке ресторана.
            var lead = row.LeadId is null ? null : await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == row.LeadId, ct);
            return Results.Ok(new CrmRestaurantCard(row,
                await db.Users.IgnoreQueryFilters().Where(u => u.RestaurantId == id).OrderBy(u => u.Login)
                    .Select(u => new CrmUser(u.Login, u.Title, u.Role)).ToListAsync(ct),
                await NotesAsync(db.CrmNotes.Where(n => n.RestaurantId == id || (row.LeadId != null && n.LeadId == row.LeadId)), ct),
                await TaskRows(db, db.CrmTasks.Where(t => t.RestaurantId == id || (row.LeadId != null && t.LeadId == row.LeadId)).OrderBy(t => t.DueAt)).ToListAsync(ct),
                lead?.Name, lead?.Contact));
        });

        crm.MapPost("/restaurants", async (RestaurantCreateRequest req, AppDbContext db, RestaurantOptions defaults, TimeProvider time, CancellationToken ct) =>
        {
            var created = await Restaurants.CreateAsync(db, req.Restaurant, req.Login, defaults, time, ct);
            return created.Error is null
                ? Results.Created("/api/crm/restaurants", new CrmAccess(created.Name, created.Login, created.Password))
                : NotCreated(created.Error);
        });

        // Клиент забыл пароль, а других управляющих в ресторане нет: новый пароль выдаёт команда продукта.
        crm.MapPost("/restaurants/{id:guid}/reset-password", async (Guid id, CrmResetRequest? req, ClaimsPrincipal staff, AppDbContext db, AuthOptions options, TimeProvider time, CancellationToken ct) =>
        {
            var login = (req?.Login ?? "").Trim().ToLowerInvariant();
            var users = db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.RestaurantId == id);
            var found = login == ""
                ? await users.Where(u => u.Role == Role.Manager).OrderBy(u => u.Login).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                : await users.Where(u => u.Login == login).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (found is null) return Results.NotFound();

            // Запись блокируется: из двух одновременных сбросов второй дождётся первого, и рабочим останется последний выданный пароль.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var user = (await db.Users.FromSql($"""SELECT * FROM "Users" WHERE "Id" = {found} FOR UPDATE""").IgnoreQueryFilters().ToListAsync(ct)).FirstOrDefault();
            // Запись могли удалить в кабинете, пока шёл поиск.
            if (user is null) return Results.NotFound();
            if (StaffEndpoints.IsDemoAccount(user, options))
                return Results.Problem("Пароль открытой записи демо-ресторана и записи поддержки сбросить нельзя.", statusCode: StatusCodes.Status409Conflict);

            var password = Restaurants.NewPassword();
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
            // Выданные токены перестают действовать: войти можно только с новым паролем.
            user.TokenVersion++;
            Log(db, staff, time, id, $"Сброшен пароль записи {user.Login}.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new CrmPassword(user.Login, password));
        });

        crm.MapPost("/restaurants/{id:guid}/disable", async (Guid id, ClaimsPrincipal staff, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            // В демо-ресторане работает сама команда продукта: отключив его, она закрыла бы вход и себе.
            if (id == Restaurant.DemoId)
                return Results.Problem("Демо-ресторан отключить нельзя.", statusCode: StatusCodes.Status409Conflict);
            if (!await db.Restaurants.AnyAsync(r => r.Id == id, ct)) return Results.NotFound();
            var now = time.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Условие в самой команде: из одновременных запросов ресторан отключает только один, остальные ничего не меняют.
            if (await db.Restaurants.Where(r => r.Id == id && r.DisabledAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisabledAt, now), ct) == 1)
            {
                // Выданные токены отменяются насовсем: после включения сотрудники входят заново.
                await db.Users.IgnoreQueryFilters().Where(u => u.RestaurantId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenVersion, u => u.TokenVersion + 1), ct);
                Log(db, staff, time, id, "Кабинет отключён.");
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });

        crm.MapPost("/restaurants/{id:guid}/enable", async (Guid id, ClaimsPrincipal staff, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (!await db.Restaurants.AnyAsync(r => r.Id == id, ct)) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.Restaurants.Where(r => r.Id == id && r.DisabledAt != null).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisabledAt, (DateTimeOffset?)null), ct) == 1)
            {
                Log(db, staff, time, id, "Кабинет включён.");
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });

        crm.MapGet("/chats", async (AppDbContext db, CancellationToken ct) =>
            await db.Chats
                .OrderByDescending(c => c.LastMessageAt)
                .Take(100)
                .Select(c => new { c.Id, c.Name, c.Contact, c.LastMessageAt, c.Unread, c.LeadId, Last = c.Messages.OrderByDescending(m => m.Id).First() })
                .Select(c => new CrmChatRow(c.Id, c.Name, c.Contact, c.LastMessageAt, c.Unread, c.Last.Text, c.Last.FromStaff, c.LeadId))
                .ToListAsync(ct));

        crm.MapPut("/chats/{id:guid}/lead", async (Guid id, CrmChatLead req, AppDbContext db, CancellationToken ct) =>
        {
            var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (chat is null) return Results.NotFound();
            if (req.LeadId is { } lead && !await db.Leads.AnyAsync(l => l.Id == lead, ct))
                return new Errors().Add("leadId", "Такой заявки нет.").ToResult();
            chat.LeadId = req.LeadId;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Посетитель написал в чат, а форму заявки не заполнял: заявка заводится из его диалога.
        crm.MapPost("/chats/{id:guid}/lead", async (Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            // Диалог блокируется до конца операции: два одновременных нажатия не заведут две заявки.
            // Заявка и привязка диалога к ней записываются вместе: номер заявки появляется только после первой записи.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var chat = (await db.Chats.FromSql($"""SELECT * FROM "Chats" WHERE "Id" = {id} FOR UPDATE""").ToListAsync(ct)).FirstOrDefault();
            if (chat is null) return Results.NotFound();
            if (chat.LeadId is not null)
                return Results.Problem("Диалог уже привязан к заявке.", statusCode: StatusCodes.Status409Conflict);

            var now = time.GetUtcNow();
            var lead = new Lead
            {
                CreatedAt = now, UpdatedAt = now, Name = chat.Name, Organization = "—", Contact = chat.Contact,
                Comment = await db.ChatMessages.Where(m => m.ChatId == id).OrderBy(m => m.Id).Select(m => m.Text).FirstAsync(ct),
            };
            db.Leads.Add(lead);
            await db.SaveChangesAsync(ct);
            chat.LeadId = lead.Id;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/crm/leads/{lead.Id}", await LeadRows(db, db.Leads.Where(l => l.Id == lead.Id)).FirstAsync(ct));
        });

        crm.MapPost("/notes", async (CrmNoteRequest req, ClaimsPrincipal user, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var text = req.Text?.Trim() ?? "";
            var errors = new Errors()
                .Check(text is { Length: > 0 and <= MaxNote }, "text", $"Напишите заметку — до {MaxNote} символов.")
                .Check(req.LeadId is null != req.RestaurantId is null, "leadId", "Заметка относится либо к заявке, либо к ресторану.");
            if ((await CheckTargetAsync(errors, db, req.LeadId, req.RestaurantId, ct)).Any) return errors.ToResult();

            var note = new CrmNote
            {
                CreatedAt = time.GetUtcNow(), Author = user.Identity!.Name!, Text = text, LeadId = req.LeadId, RestaurantId = req.RestaurantId,
            };
            db.CrmNotes.Add(note);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/crm/notes/{note.Id}", ToResponse(note));
        });

        // Удаление одной командой: из двух одновременных запросов второй получает «не найдено», а не сбой.
        crm.MapDelete("/notes/{id:long}", async (long id, AppDbContext db, EventBus bus, CancellationToken ct) =>
            Deleted(await db.CrmNotes.Where(n => n.Id == id).ExecuteDeleteAsync(ct), db, bus));

        crm.MapGet("/tasks", async (bool? open, AppDbContext db, CancellationToken ct) =>
            await TaskRows(db, db.CrmTasks.Where(t => open != true || t.DoneAt == null).OrderBy(t => t.DueAt).Take(500)).ToListAsync(ct));

        crm.MapPost("/tasks", async (CrmTaskRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var title = req.Title?.Trim() ?? "";
            var errors = CheckTask(title, req.DueAt)
                .Check(req.LeadId is null || req.RestaurantId is null, "leadId", "Задача относится либо к заявке, либо к ресторану.");
            if ((await CheckTargetAsync(errors, db, req.LeadId, req.RestaurantId, ct)).Any) return errors.ToResult();

            var task = new CrmTask
            {
                // База хранит время в UTC; срок приходит с часовым поясом того, кто его назначил.
                CreatedAt = time.GetUtcNow(), Title = title, DueAt = req.DueAt!.Value.ToUniversalTime(), LeadId = req.LeadId, RestaurantId = req.RestaurantId,
            };
            db.CrmTasks.Add(task);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/crm/tasks/{task.Id}", await TaskRows(db, db.CrmTasks.Where(t => t.Id == task.Id)).FirstAsync(ct));
        });

        crm.MapPut("/tasks/{id:long}", async (long id, CrmTaskUpdate req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var title = req.Title?.Trim() ?? "";
            var errors = CheckTask(title, req.DueAt);
            if (errors.Any) return errors.ToResult();

            var task = await db.CrmTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (task is null) return Results.NotFound();
            (task.Title, task.DueAt) = (title, req.DueAt!.Value.ToUniversalTime());
            // Повторная отметка не сдвигает время, когда задачу закрыли на самом деле.
            if (req.Done is { } done) task.DoneAt = done ? task.DoneAt ?? time.GetUtcNow() : null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await TaskRows(db, db.CrmTasks.Where(t => t.Id == id)).FirstAsync(ct));
        });

        crm.MapDelete("/tasks/{id:long}", async (long id, AppDbContext db, EventBus bus, CancellationToken ct) =>
            Deleted(await db.CrmTasks.Where(t => t.Id == id).ExecuteDeleteAsync(ct), db, bus));
    }

    private static IQueryable<CrmLeadRow> LeadRows(AppDbContext db, IQueryable<Lead> leads) => leads.Select(l => new CrmLeadRow(
        l.Id, l.CreatedAt, l.UpdatedAt, l.Name, l.Organization, l.Contact, l.Points, l.Comment, l.Status, l.Assignee, l.RestaurantId,
        db.Restaurants.Where(r => r.Id == l.RestaurantId).Select(r => r.Name).FirstOrDefault(),
        db.Chats.Where(c => c.LeadId == l.Id).OrderByDescending(c => c.LastMessageAt).Select(c => (Guid?)c.Id).FirstOrDefault(),
        db.CrmNotes.Count(n => n.LeadId == l.Id),
        db.CrmTasks.Count(t => t.LeadId == l.Id && t.DoneAt == null)));

    /// <summary>
    /// Строки ресторанов со счётчиками. Единственное место, где запрос снимает фильтр по ресторану с данных склада,
    /// и берёт он оттуда только количества и даты.
    /// </summary>
    private static IQueryable<CrmRestaurantRow> RestaurantRows(AppDbContext db, IQueryable<Restaurant> restaurants, DateTimeOffset now)
    {
        var since = now.AddDays(-7);
        return restaurants.IgnoreQueryFilters().Select(r => new CrmRestaurantRow(
            r.Id, r.Name, r.CreatedAt, r.Id == Restaurant.DemoId, r.DisabledAt,
            db.Users.Count(u => u.RestaurantId == r.Id),
            // «Первый управляющий» — первый по алфавиту: порядок, в котором заводили сотрудников, база не хранит.
            db.Users.Where(u => u.RestaurantId == r.Id && u.Role == Role.Manager).OrderBy(u => u.Login).Select(u => u.Login).FirstOrDefault() ?? r.OwnerLogin,
            db.Products.Count(p => p.RestaurantId == r.Id),
            db.Orders.Count(o => o.RestaurantId == r.Id && o.CreatedAt >= since),
            db.Movements.Where(m => m.RestaurantId == r.Id && m.Actor != WriteOffService.System).Max(m => (DateTimeOffset?)m.At),
            db.Leads.Where(l => l.RestaurantId == r.Id).OrderBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefault(),
            db.CrmNotes.Count(n => n.RestaurantId == r.Id),
            db.CrmTasks.Count(t => t.RestaurantId == r.Id && t.DoneAt == null)));
    }

    private static IQueryable<CrmTaskResponse> TaskRows(AppDbContext db, IQueryable<CrmTask> tasks) => tasks.Select(t => new CrmTaskResponse(
        t.Id, t.CreatedAt, t.Title, t.DueAt, t.DoneAt,
        t.LeadId, db.Leads.Where(l => l.Id == t.LeadId).Select(l => l.Name).FirstOrDefault(),
        t.RestaurantId, db.Restaurants.Where(r => r.Id == t.RestaurantId).Select(r => r.Name).FirstOrDefault()));

    private static async Task<List<CrmNoteResponse>> NotesAsync(IQueryable<CrmNote> notes, CancellationToken ct) =>
        (await notes.OrderByDescending(n => n.Id).ToListAsync(ct)).Select(ToResponse).ToList();

    private static CrmNoteResponse ToResponse(CrmNote n) => new(n.Id, n.CreatedAt, n.Author, n.Text, n.LeadId, n.RestaurantId);

    private static Errors CheckTask(string title, DateTimeOffset? dueAt) => new Errors()
        .Check(title is { Length: > 0 and <= 200 }, "title", "Что нужно сделать? До 200 символов.")
        .Check(dueAt is not null, "dueAt", "Укажите срок.");

    /// <summary>Заявка или ресторан, на которые ссылается заметка или задача, должны существовать.</summary>
    private static async Task<Errors> CheckTargetAsync(Errors errors, AppDbContext db, long? leadId, Guid? restaurantId, CancellationToken ct)
    {
        if (leadId is { } lead && !await db.Leads.AnyAsync(l => l.Id == lead, ct)) errors.Add("leadId", "Такой заявки нет.");
        if (restaurantId is { } restaurant && !await db.Restaurants.AnyAsync(r => r.Id == restaurant, ct)) errors.Add("restaurantId", "Такого ресторана нет.");
        return errors;
    }

    private static IResult Deleted(int rows, AppDbContext db, EventBus bus)
    {
        if (rows == 0) return Results.NotFound();
        bus.Publish(db.TenantId);
        return Results.NoContent();
    }

    /// <summary>Сброс пароля и отключение дают доступ к чужому кабинету или отнимают его: кто и когда это сделал, остаётся заметкой о ресторане.</summary>
    private static void Log(AppDbContext db, ClaimsPrincipal staff, TimeProvider time, Guid restaurant, string text) =>
        db.CrmNotes.Add(new CrmNote { CreatedAt = time.GetUtcNow(), Author = staff.Identity!.Name!, Text = text, RestaurantId = restaurant });

    private static IResult NotCreated(string error) =>
        Results.Problem(error, statusCode: error.Contains("занят") ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
}
