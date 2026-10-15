using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

/// <summary>
/// Зал демо-ресторана: три зоны, 12 столов на 2–8 мест, гости за столами, брони на вечер, стол на уборке и закрытый стол.
/// Посадки и брони заданы от текущего момента. Если в зале сутки ничего не происходило (демо давно не открывали),
/// при запуске сцена разыгрывается заново: зависшие визиты завершаются, появляются свежие гости и брони.
/// </summary>
public static class HallDemo
{
    public const string Actor = "демо";

    public static async Task SeedAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!await db.HallZones.AnyAsync(ct))
        {
            var main = new HallZone { Id = Guid.NewGuid(), Name = "Основной зал", Sort = 0 };
            var terrace = new HallZone { Id = Guid.NewGuid(), Name = "Терраса", Sort = 1 };
            var vip = new HallZone { Id = Guid.NewGuid(), Name = "VIP", Sort = 2 };
            db.HallZones.AddRange(main, terrace, vip);
            HallTable T(HallZone zone, int number, int seats, TableShape shape, int x, int y)
            {
                var (w, h) = (shape, seats) switch { (TableShape.Round, _) => (13, 18), (_, <= 4) => (16, 18), _ => (22, 20) };
                return new HallTable { Id = Guid.NewGuid(), ZoneId = zone.Id, Number = number, Seats = seats, Shape = shape, X = x, Y = y, W = w, H = h };
            }
            db.HallTables.AddRange(
                T(main, 1, 2, TableShape.Round, 6, 10), T(main, 2, 2, TableShape.Round, 28, 10), T(main, 3, 4, TableShape.Rect, 50, 10),
                T(main, 4, 4, TableShape.Rect, 74, 10), T(main, 5, 4, TableShape.Rect, 6, 55), T(main, 6, 6, TableShape.Rect, 30, 55),
                T(main, 7, 2, TableShape.Round, 60, 55),
                T(terrace, 8, 4, TableShape.Round, 10, 20), T(terrace, 9, 4, TableShape.Round, 40, 20), T(terrace, 10, 6, TableShape.Rect, 68, 20),
                T(vip, 11, 8, TableShape.Rect, 20, 30), T(vip, 12, 4, TableShape.Round, 60, 30));
            await db.SaveChangesAsync(ct);
        }
        else if (await db.HallEvents.AnyAsync(e => e.At > now.AddHours(-24), ct))
        {
            return;
        }

        var tables = await db.HallTables.Where(t => !t.Archived).ToDictionaryAsync(t => t.Number, ct);
        if (!new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }.All(tables.ContainsKey)) return;

        // Зависшее с прошлого показа: гости «уходят», столы готовы, просроченные брони отмечаются неявкой.
        foreach (var v in await db.Visits.Where(v => v.EndedAt == null || v.ReadyAt == null).ToListAsync(ct))
            (v.EndedAt, v.ReadyAt, v.EndedBy, v.ReadyBy) = (v.EndedAt ?? now, now, v.EndedBy ?? Actor, Actor);
        foreach (var r in await db.Reservations.Where(r => r.Status == ReservationStatus.Seated || (r.Status == ReservationStatus.Confirmed && r.StartsAt < now)).ToListAsync(ct))
            r.Status = r.Status == ReservationStatus.Seated ? ReservationStatus.Completed : ReservationStatus.NoShow;

        // Время сцены кратно пяти минутам — как в настоящей книге броней.
        var at = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero);
        Reservation R(int table, string guest, string? phone, int guests, int fromMinutes, int minutes, ReservationStatus status, string? note = null) => new()
        {
            Id = Guid.NewGuid(), TableId = tables[table].Id, Guest = guest, Phone = phone, Guests = guests,
            StartsAt = at.AddMinutes(fromMinutes), EndsAt = at.AddMinutes(fromMinutes + minutes), Status = status, Note = note,
            CreatedAt = now.AddDays(-1), CreatedBy = "manager",
        };
        Visit V(int table, int guests, int seatedMinutesAgo, int expectedInMinutes, Guid? reservation = null) => new()
        {
            Id = Guid.NewGuid(), TableId = tables[table].Id, ReservationId = reservation, Guests = guests,
            SeatedAt = at.AddMinutes(-seatedMinutesAgo), ExpectedEndAt = at.AddMinutes(expectedInMinutes), SeatedBy = "waiter",
        };

        var seated = R(6, "Ирина Соколова", "+7 912 555-14-20", 5, -75, 120, ReservationStatus.Seated, "День рождения, торт свой");
        var late = R(7, "Олег", "+7 903 111-22-33", 2, 20, 90, ReservationStatus.Confirmed);
        db.Reservations.AddRange(
            seated, late,
            R(5, "Мария К.", "+7 916 200-30-40", 3, 20, 120, ReservationStatus.Confirmed, "Столик у окна"),
            R(2, "Александр П.", null, 2, 180, 90, ReservationStatus.Confirmed),
            R(10, "Компания Петровых", "+7 925 010-20-30", 6, 240, 150, ReservationStatus.Confirmed, "Терраса, если тепло"),
            R(11, "Деловой ужин «Север»", "+7 495 777-00-11", 8, 300, 180, ReservationStatus.Confirmed, "Нужен проектор"),
            R(8, "Елена В.", null, 4, 24 * 60, 120, ReservationStatus.Confirmed),
            R(3, "Сергей Н.", "+7 999 800-70-60", 2, -300, 90, ReservationStatus.Completed),
            R(5, "Анна", null, 2, -240, 60, ReservationStatus.Cancelled),
            R(8, "Дмитрий", "+7 901 234-56-78", 4, -180, 120, ReservationStatus.NoShow));
        db.Visits.AddRange(
            V(3, 3, 40, 80),
            V(6, 5, 70, 45, seated.Id),
            // Гости засиделись: ожидаемое время прошло, а в 20 минут бронь — стол показывает риск.
            V(7, 2, 130, -10),
            new Visit { Id = Guid.NewGuid(), TableId = tables[4].Id, Guests = 4, SeatedAt = at.AddMinutes(-95), ExpectedEndAt = at.AddMinutes(-5), EndedAt = at.AddMinutes(-5), SeatedBy = "waiter", EndedBy = "waiter" });
        db.TableBlocks.Add(new TableBlock { Id = Guid.NewGuid(), TableId = tables[9].Id, From = now, To = at.AddDays(1), Reason = "Ремонт зонта над столом", CreatedAt = now, CreatedBy = "manager" });
        db.HallEvents.Add(new HallEvent { At = now, Actor = Actor, Entity = "table", Action = "demo", Text = "Демо-смена зала: гости за столами, брони на вечер, стол на уборке и закрытый стол" });
        await db.SaveChangesAsync(ct);
    }
}
