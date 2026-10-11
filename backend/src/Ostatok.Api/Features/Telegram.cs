using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>Бот поддержки. Токен задаётся переменной окружения Telegram__BotToken; без него бот выключен.</summary>
public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public string ApiBase { get; set; } = "https://api.telegram.org";
}

/// <param name="RepliedText">Текст сообщения, на которое ответили: по нему бот понимает, какому посетителю адресован ответ.</param>
public sealed record TelegramUpdate(long Id, long ChatId, string From, string Text, string? RepliedText);

public interface ITelegramApi
{
    bool Enabled { get; }
    Task SendAsync(long chatId, string text, CancellationToken ct);
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken ct);
}

public sealed class TelegramApi(IHttpClientFactory http, TelegramOptions options) : ITelegramApi
{
    public bool Enabled => options.BotToken != "";

    private string Url(string method) => $"{options.ApiBase}/bot{options.BotToken}/{method}";

    public async Task SendAsync(long chatId, string text, CancellationToken ct)
    {
        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        // Без разметки: текст посетителя уходит как есть и не может сломать сообщение.
        using var response = await client.PostAsJsonAsync(Url("sendMessage"), new { chat_id = chatId, text, disable_web_page_preview = true }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken ct)
    {
        using var client = http.CreateClient();
        // Долгий опрос: Telegram держит запрос до 25 секунд и отвечает сразу, как появится сообщение.
        client.Timeout = TimeSpan.FromSeconds(40);
        var body = await client.GetFromJsonAsync<JsonNode>($"{Url("getUpdates")}?timeout=25&offset={offset}&allowed_updates=%5B%22message%22%5D", ct);
        var updates = new List<TelegramUpdate>();
        foreach (var item in body?["result"]?.AsArray() ?? [])
        {
            var message = item?["message"];
            var id = item?["update_id"]?.GetValue<long>() ?? 0;
            // Обновление без текста (фото, стикер) всё равно учитывается, чтобы не получать его снова.
            updates.Add(new TelegramUpdate(
                id,
                message?["chat"]?["id"]?.GetValue<long>() ?? 0,
                message?["from"]?["first_name"]?.GetValue<string>() ?? "",
                message?["text"]?.GetValue<string>() ?? "",
                message?["reply_to_message"]?["text"]?.GetValue<string>()));
        }
        return updates;
    }
}

/// <summary>
/// Поддержка в Telegram: заявки и вопросы с лендинга приходят операторам, ответ на сообщение уходит посетителю,
/// командой создаётся ресторан для нового клиента. Оператором становится тот, кто прислал боту пароль поддержки.
/// </summary>
public sealed partial class SupportDesk(
    AppDbContext db, ITelegramApi telegram, AuthOptions auth, LoginAttempts attempts, RestaurantOptions defaults, TimeProvider time,
    IConfiguration config, ILogger<SupportDesk> log)
{
    [GeneratedRegex("#([0-9a-f]{32})")]
    private static partial Regex ChatTag();

    private const string Help =
        "Я присылаю заявки и вопросы с сайта.\n\n" +
        "• Ответить посетителю — ответьте на сообщение с его вопросом (свайп по сообщению → «Ответить»).\n" +
        "• Создать кабинет ресторана — /new Название ресторана; логин\n" +
        "  Пример: /new Траттория на углу; trattoria.admin\n" +
        "• Перестать получать сообщения — /stop";

    /// <summary>Рассылает сообщение операторам. Сбой Telegram не мешает посетителю: заявка уже сохранена.</summary>
    public async Task NotifyAsync(string text, CancellationToken ct)
    {
        if (!telegram.Enabled) return;
        var chats = await db.TelegramOperators.Select(o => o.ChatId).ToListAsync(ct);
        // Отправка идёт в фоне: посетитель не ждёт ответа Telegram.
        _ = Task.Run(async () =>
        {
            foreach (var chat in chats)
            {
                try
                {
                    await telegram.SendAsync(chat, text, CancellationToken.None);
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "Сообщение в Telegram не отправлено");
                }
            }
        }, CancellationToken.None);
    }

    public Task LeadAsync(Lead lead, CancellationToken ct) => NotifyAsync(
        $"Новая заявка на демо\n\nИмя: {lead.Name}\nРесторан: {lead.Organization}\nКонтакт: {lead.Contact}\nТочек: {lead.Points}" +
        (lead.Comment is null ? "" : $"\nКомментарий: {lead.Comment}") +
        "\n\nСоздать кабинет: /new Название ресторана; логин", ct);

    public Task QuestionAsync(Chat chat, string text, CancellationToken ct) => NotifyAsync(
        $"Вопрос с сайта\n\n{chat.Name} · {chat.Contact}\n\n{text}\n\nОтветьте на это сообщение — ответ увидит посетитель.\n#{chat.Id:N}", ct);

    /// <summary>Обрабатывает сообщение, пришедшее боту.</summary>
    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        if (update.ChatId == 0 || update.Text == "") return;
        var text = update.Text.Trim();
        var isOperator = await db.TelegramOperators.AnyAsync(o => o.ChatId == update.ChatId, ct);

        if (text.StartsWith("/start"))
        {
            var code = text["/start".Length..].Trim();
            // Пароль поддержки нельзя перебирать через бота: после нескольких неверных попыток чат блокируется на время,
            // как и вход на сайте.
            var (who, where) = ("telegram", update.ChatId.ToString());
            if (isOperator) await Reply("Вы уже получаете заявки и вопросы.\n\n" + Help);
            else if (attempts.LockedMinutes(who, where) is > 0 and var minutes)
                await Reply($"Слишком много неверных попыток. Попробуйте через {minutes} мин.");
            else if (code != "" && auth.SupportPassword != "" && CryptographicOperations.FixedTimeEquals(
                         System.Text.Encoding.UTF8.GetBytes(code), System.Text.Encoding.UTF8.GetBytes(auth.SupportPassword)))
            {
                attempts.Reset(who, where);
                db.TelegramOperators.Add(new TelegramOperator { ChatId = update.ChatId, Name = update.From, AddedAt = time.GetUtcNow() });
                await db.SaveChangesAsync(ct);
                await Reply("Готово: заявки и вопросы с сайта будут приходить сюда.\nСообщение с паролем лучше удалить из переписки.\n\n" + Help);
            }
            else
            {
                if (code != "") attempts.Fail(who, where);
                await Reply("Это служебный бот поддержки «Остатка». Чтобы получать заявки, отправьте: /start <пароль поддержки>");
            }
            return;
        }

        // Всё остальное — только для операторов; посторонним бот не отвечает ничем, кроме приглашения выше.
        if (!isOperator) return;

        if (text == "/stop")
        {
            await db.TelegramOperators.Where(o => o.ChatId == update.ChatId).ExecuteDeleteAsync(ct);
            await Reply("Больше не присылаю заявки и вопросы. Вернуться: /start <пароль поддержки>");
        }
        else if (text.StartsWith("/new")) await Reply(await NewRestaurantAsync(text["/new".Length..], ct));
        // Метку диалога сервер ставит в конце сообщения. Берётся последняя: похожая «метка» в тексте посетителя
        // не должна увести ответ в другой диалог.
        else if (update.RepliedText is { } replied && ChatTag().Matches(replied).LastOrDefault() is { } tag)
            await Reply(await AnswerAsync(Guid.ParseExact(tag.Groups[1].Value, "N"), text, update.From, ct));
        else await Reply(Help);

        Task Reply(string message) => telegram.SendAsync(update.ChatId, message, ct);
    }

    private async Task<string> AnswerAsync(Guid chatId, string text, string from, CancellationToken ct)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return "Этот диалог не найден — возможно, он удалён.";
        if (text.Length > 1000) return "Ответ длиннее 1000 символов — сократите или разбейте на два.";

        var now = time.GetUtcNow();
        db.ChatMessages.Add(new ChatMessage { ChatId = chatId, At = now, FromStaff = true, Author = $"telegram:{from}"[..Math.Min(100, 9 + from.Length)], Text = text });
        (chat.LastMessageAt, chat.Unread) = (now, 0);
        await db.SaveChangesAsync(ct);
        return $"Отправлено: {chat.Name} увидит ответ в чате на сайте.";
    }

    private async Task<string> NewRestaurantAsync(string arguments, CancellationToken ct)
    {
        var parts = arguments.Split(';', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return "Формат: /new Название ресторана; логин\nПример: /new Траттория на углу; trattoria.admin";

        var created = await Restaurants.CreateAsync(db, parts[0], parts[1], defaults, time, ct);
        return created.Error ?? $"Ресторан «{created.Name}» создан.\n\nАдрес: {config["Site:Url"] ?? "https://ostatok.onrender.com"}/login\nЛогин: {created.Login}\nПароль: {created.Password}\n\n" +
            "Передайте клиенту. Он входит управляющим и сам заводит сотрудников.";
    }
}

public sealed record NewRestaurant(string? Error, string Name = "", string Login = "", string Password = "");

/// <summary>Создание ресторана: это делает команда продукта после заявки, а не посетитель сайта.</summary>
public static class Restaurants
{
    private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static async Task<NewRestaurant> CreateAsync(
        AppDbContext db, string? name, string? login, RestaurantOptions defaults, TimeProvider time, CancellationToken ct)
    {
        (name, login) = ((name ?? "").Trim(), (login ?? "").Trim().ToLowerInvariant());
        if (name is not { Length: > 0 and <= 120 }) return new("Укажите название ресторана — до 120 символов.");
        if (!StaffEndpoints.LoginPattern().IsMatch(login)) return new(StaffEndpoints.LoginRule);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Login == login, ct)) return new("Такой логин уже занят.");

        // Пароль выдаёт система: его нельзя выбрать слабым, а знает его только тот, кто создал ресторан.
        var password = RandomNumberGenerator.GetString(PasswordAlphabet, 12);
        var restaurant = new Restaurant
        {
            Id = Guid.NewGuid(), Name = name, CreatedAt = time.GetUtcNow(), ShiftStart = defaults.ShiftStart, ShiftEnd = defaults.ShiftEnd,
        };
        var user = new User { Id = Guid.NewGuid(), RestaurantId = restaurant.Id, Login = login, PasswordHash = "", Title = "Управляющий", Role = Role.Manager };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, password);
        db.Restaurants.Add(restaurant);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return new(null, name, login, password);
    }
}

/// <summary>Забирает сообщения у Telegram и передаёт их в <see cref="SupportDesk"/>.</summary>
public sealed class TelegramBot(IServiceScopeFactory scopes, ITelegramApi telegram, ILogger<TelegramBot> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!telegram.Enabled) return;
        long offset = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var update in await telegram.GetUpdatesAsync(offset, stop))
                {
                    // Номер запоминается до обработки: сбой на одном сообщении не должен зациклить бота на нём.
                    offset = update.Id + 1;
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<SupportDesk>().HandleAsync(update, stop);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogError(e, "Сообщение из Telegram не обработано");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // В тексте ошибки HttpClient есть адрес с токеном — в журнал он попасть не должен.
                log.LogWarning("Telegram недоступен: {Error}", e.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stop).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }
}

public sealed record RestaurantCreateRequest(string? Restaurant, string? Login);

public static class SupportEndpoints
{
    public static void MapSupportDesk(this RouteGroupBuilder api)
    {
        // Кабинет для нового клиента создаёт команда продукта: из раздела «Обращения» или командой в боте.
        api.MapPost("/restaurants", async (RestaurantCreateRequest req, AppDbContext db, RestaurantOptions defaults, TimeProvider time, CancellationToken ct) =>
        {
            var created = await Restaurants.CreateAsync(db, req.Restaurant, req.Login, defaults, time, ct);
            return created.Error is null
                ? Results.Created("/api/me", created)
                : Results.Problem(created.Error, statusCode: created.Error.Contains("занят") ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
        })
        .WithTags("Обращения")
        .RequireRoles(Access.Leads)
        .VendorOnly();
    }
}
