using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="AutoWriteOff">Списывать просрочку без человека; пусто в запросе — не менять.</param>
/// <param name="AutoWriteOffAfterHours">Через сколько часов после истечения срока спишется сама; пусто в запросе — не менять.</param>
/// <param name="HasIntegrationKey">Выдан ли ключ интеграции для поставок извне; в запросе не учитывается.</param>
public sealed record RestaurantSettings(
    string? Name, string? ShiftStart, string? ShiftEnd, int Tables, bool HasBar,
    bool? AutoWriteOff = null, int? AutoWriteOffAfterHours = null, bool HasIntegrationKey = false);

/// <param name="Key">Ключ показывается один раз: в базе остаётся только его хеш.</param>
public sealed record IntegrationKeyResponse(string Key, Guid RestaurantId);

/// <summary>Настройки ресторана: название, часы смены и зал. У каждого ресторана они свои.</summary>
public static partial class RestaurantEndpoints
{
    [GeneratedRegex(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$|^24:00$")]
    private static partial Regex ClockTime();

    public static void MapRestaurant(this RouteGroupBuilder api)
    {
        var restaurant = api.MapGroup("/restaurant").WithTags("Ресторан").RequireRoles(Access.Staff);

        restaurant.MapGet("", async (AppDbContext db, CancellationToken ct) => ToSettings(await db.CurrentRestaurantAsync(ct)));

        restaurant.MapPut("", async (RestaurantSettings req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            // «9:00» — то же, что «09:00»: в базе время хранится в одном виде.
            var (name, start, end) = ((req.Name ?? "").Trim(), (req.ShiftStart ?? "").Trim().PadLeft(5, '0'), (req.ShiftEnd ?? "").Trim().PadLeft(5, '0'));
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "name", "Укажите название ресторана — до 120 символов.")
                .Check(ClockTime().IsMatch(start) && ClockTime().IsMatch(end) && start != "24:00", "shift", "Время смены — в формате ЧЧ:ММ: начало не позже 23:59, конец не позже 24:00.")
                .Check(req.Tables is >= 0 and <= 60, "tables", "Столов — от 0 до 60.")
                .Check(req.Tables > 0 || req.HasBar, "tables", "В зале должен быть хотя бы один стол или бар.")
                .Check(req.AutoWriteOffAfterHours is null or (>= 1 and <= 168), "autoWriteOffAfterHours", "Отсрочка автосписания — от 1 до 168 часов: так просрочку успевают увидеть и списать вручную.");
            if (errors.Any) return errors.ToResult();
            // Конец раньше начала — смена через полночь (18:00–02:00), это допустимо; совпадать они не могут.
            if (StopCost.ParseTime(end) == StopCost.ParseTime(start))
                return errors.Add("shift", "Начало и конец смены не должны совпадать.").ToResult();

            var places = new Restaurant { Name = name, Tables = req.Tables, HasBar = req.HasBar }.Places();
            var busy = await OrderEndpoints.OpenOrders(db, time.GetUtcNow()).Select(o => o.Place).Distinct().ToListAsync(ct);
            if (busy.Except(places).Order().ToList() is { Count: > 0 } left)
                return errors.Add("tables", $"Сначала закройте открытые заказы: {string.Join(", ", left)}.").ToResult();

            var row = await db.Restaurants.FirstAsync(r => r.Id == db.TenantId, ct);
            (row.Name, row.ShiftStart, row.ShiftEnd, row.Tables, row.HasBar) = (name, start, end, req.Tables, req.HasBar);
            if (req.AutoWriteOff is { } auto) row.AutoWriteOff = auto;
            if (req.AutoWriteOffAfterHours is { } hours) row.AutoWriteOffAfterHours = hours;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToSettings(row));
        });

        // Ключ интеграции: с ним учётная система или сервис поставщика присылает поставки (POST /api/supplies/incoming,
        // заголовок X-Api-Key). Новый ключ сразу заменяет прежний — старый перестаёт работать.
        restaurant.MapPost("/integration-key", async (AppDbContext db, CancellationToken ct) =>
        {
            var row = await db.Restaurants.FirstAsync(r => r.Id == db.TenantId, ct);
            var key = IntegrationKey.Create();
            row.IntegrationKeyHash = IntegrationKey.Hash(key);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new IntegrationKeyResponse(key, row.Id));
        });
    }

    private static RestaurantSettings ToSettings(Restaurant r) =>
        new(r.Name, r.ShiftStart, r.ShiftEnd, r.Tables, r.HasBar, r.AutoWriteOff, r.AutoWriteOffAfterHours, r.IntegrationKeyHash is not null);
}

/// <summary>Ключ интеграции ресторана для внешних систем. Хранится только SHA-256: ключ случайный и длинный, подбирать его бессмысленно.</summary>
public static class IntegrationKey
{
    public const string Header = "X-Api-Key";

    public static string Create() => "ost_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    /// <summary>Ресторан, которому выдан этот ключ; null — ключа нет, он неверный или ресторан отключён.</summary>
    public static async Task<Guid?> FindRestaurantAsync(AppDbContext db, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100) return null;
        var hash = Hash(key);
        return await db.Restaurants.Where(r => r.IntegrationKeyHash == hash && r.DisabledAt == null).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
    }
}
