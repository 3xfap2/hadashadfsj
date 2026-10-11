using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record RestaurantSettings(string? Name, string? ShiftStart, string? ShiftEnd, int Tables, bool HasBar);

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
                .Check(req.Tables > 0 || req.HasBar, "tables", "В зале должен быть хотя бы один стол или бар.");
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
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToSettings(row));
        });
    }

    private static RestaurantSettings ToSettings(Restaurant r) => new(r.Name, r.ShiftStart, r.ShiftEnd, r.Tables, r.HasBar);
}
