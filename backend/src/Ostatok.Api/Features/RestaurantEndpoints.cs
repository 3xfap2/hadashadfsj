using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record RestaurantSettings(string? Name, string? ShiftStart, string? ShiftEnd, int Tables, bool HasBar);

/// <summary>Настройки ресторана: название, часы смены и зал. У каждого ресторана они свои.</summary>
public static partial class RestaurantEndpoints
{
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$|^24:00$")]
    private static partial Regex ClockTime();

    public static void MapRestaurant(this RouteGroupBuilder api)
    {
        var restaurant = api.MapGroup("/restaurant").WithTags("Ресторан").RequireRoles(Access.Staff);

        restaurant.MapGet("", async (AppDbContext db, CancellationToken ct) => ToSettings(await db.CurrentRestaurantAsync(ct)));

        restaurant.MapPut("", async (RestaurantSettings req, AppDbContext db, CancellationToken ct) =>
        {
            var (name, start, end) = ((req.Name ?? "").Trim(), req.ShiftStart ?? "", req.ShiftEnd ?? "");
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "name", "Укажите название ресторана — до 120 символов.")
                .Check(ClockTime().IsMatch(start) && ClockTime().IsMatch(end), "shift", "Время смены — в формате ЧЧ:ММ, конец не позже 24:00.")
                .Check(req.Tables is >= 0 and <= 60, "tables", "Столов — от 0 до 60.")
                .Check(req.Tables > 0 || req.HasBar, "tables", "В зале должен быть хотя бы один стол или бар.");
            if (errors.Any) return errors.ToResult();
            if (StopCost.ParseTime(end) <= StopCost.ParseTime(start))
                return errors.Add("shift", "Смена должна заканчиваться позже, чем начинается.").ToResult();

            var row = await db.Restaurants.FirstAsync(r => r.Id == db.TenantId, ct);
            (row.Name, row.ShiftStart, row.ShiftEnd, row.Tables, row.HasBar) = (name, start, end, req.Tables, req.HasBar);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToSettings(row));
        });
    }

    private static RestaurantSettings ToSettings(Restaurant r) => new(r.Name, r.ShiftStart, r.ShiftEnd, r.Tables, r.HasBar);
}
