using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>Дата и время — по часам ресторана («2026-10-15», «19:00»), а не браузера.</summary>
/// <param name="Id">Идентификатор, который придумал клиент: повтор запроса (двойное нажатие) вернёт ту же бронь.</param>
/// <param name="Version">Версия брони из списка; при изменении обязательна — устаревшая правка получит 409.</param>
public sealed record ReservationRequest(
    Guid? Id, Guid TableId, string? Guest, string? Phone, int Guests, DateOnly Date, TimeOnly Time, int Minutes, string? Note = null, int? Version = null);

/// <param name="Id">Идентификатор визита от клиента: повтор посадки не посадит гостей второй раз.</param>
/// <param name="Minutes">На сколько минут посадить; пусто — длительность визита из настроек.</param>
public sealed record SeatRequest(Guid? Id, int Guests, int? Minutes = null);
public sealed record ExtendRequest(int Minutes);
/// <param name="Date">Начало блокировки; пусто — сейчас.</param>
/// <param name="UntilDate">Конец блокировки; пусто — до снятия.</param>
/// <param name="CancelReservations">Отменить брони, попавшие в период. Без этого блокировка с бронями отклоняется со списком броней.</param>
public sealed record BlockRequest(DateOnly? Date, TimeOnly? Time, DateOnly? UntilDate, TimeOnly? UntilTime, string? Reason, bool CancelReservations = false);
public sealed record ZoneRequest(string? Name, int Sort = 0, bool Archived = false);
/// <param name="X">Положение и размер на схеме — в процентах ширины и высоты зала.</param>
public sealed record TableRequest(Guid ZoneId, int Number, int Seats, TableShape Shape, int X, int Y, int W, int H, bool Archived = false, int? Version = null);
public sealed record HallSettingsDto(int VisitMinutes, int BufferMinutes, int NoShowMinutes, int WarnMinutes);

public sealed record ReservationView(
    Guid Id, Guid TableId, int TableNumber, string Zone, string Guest, string? Phone, int Guests,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, ReservationStatus Status, string? Note, int Version, DateTimeOffset NoShowFrom, Guid? VisitId);
public sealed record VisitView(Guid Id, Guid TableId, Guid? ReservationId, string? Guest, int Guests, DateTimeOffset SeatedAt, DateTimeOffset ExpectedEndAt, DateTimeOffset? EndedAt, string SeatedBy);
public sealed record BlockView(Guid Id, DateTimeOffset From, DateTimeOffset? To, string Reason, string CreatedBy);
public sealed record TableOrder(int Number, decimal Total, DateTimeOffset CreatedAt, bool Ready);
/// <param name="Label">Состояние словами на момент среза: «Свободен до 19:45», «Занят до 20:30».</param>
/// <param name="Warning">Риск, который сотрудник должен видеть: следующая бронь под угрозой, гость опаздывает.</param>
/// <param name="Orders">Открытые заказы стола (место заказа «Стол N»).</param>
public sealed record TableView(
    Guid Id, Guid ZoneId, int Number, int Seats, TableShape Shape, int X, int Y, int W, int H, int Version,
    TableState State, string Label, string? Warning, VisitView? Visit, ReservationView? Next, BlockView? Block,
    DateTimeOffset? CleaningSince, DateTimeOffset? FreeUntil, IEnumerable<ReservationView> Upcoming, IEnumerable<TableOrder> Orders);
public sealed record ZoneView(Guid Id, string Name, int Sort);
/// <param name="Live">Срез на текущий момент (true) или планирование на выбранные дату и время.</param>
/// <param name="UtcOffsetMinutes">Часовой пояс ресторана: по нему сайт показывает время.</param>
/// <param name="CanManage">Может ли сотрудник менять схему, настройки и закрывать столы.</param>
public sealed record HallView(
    DateTimeOffset Now, DateTimeOffset At, bool Live, string Date, string Time, int UtcOffsetMinutes, HallSettingsDto Settings,
    IEnumerable<ZoneView> Zones, IEnumerable<TableView> Tables, IEnumerable<ReservationView> Reservations, bool CanManage);
public sealed record TableOption(Guid TableId, int Number, Guid ZoneId, string Zone, int Seats, bool Fits, string? Reason);
public sealed record HallEventRow(long Id, DateTimeOffset At, string Actor, string Entity, Guid? EntityId, string Action, string Text);

/// <summary>
/// «Зал»: схема столов, брони, посадка, уборка и блокировки. Состояние стола не хранится, а вычисляется из визитов,
/// броней и блокировок. Каждая команда, меняющая занятость стола, блокирует строку стола (FOR UPDATE) до конца
/// транзакции: две одновременные брони или посадки за один стол выстраиваются в очередь, и вторая видит первую.
/// </summary>
public static class HallEndpoints
{
    public const int MaxTableNumber = 60;   // как число столов в настройках ресторана: номер стола — место заказа

    public static void MapHall(this RouteGroupBuilder api)
    {
        var hall = api.MapGroup("/hall").WithTags("Зал").RequireRoles(Access.Hall);

        // Срез зала: без даты и времени — сейчас, с ними — планирование на выбранный момент (по часам ресторана).
        hall.MapGet("", (AppDbContext db, TimeProvider clock, StockOptions options, ClaimsPrincipal user, CancellationToken ct, string? date = null, string? time = null) =>
            new Hall(db, clock, options, user).ViewAsync(date, time, ct));

        hall.MapGet("/reservations", async (AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct,
            string? date = null, string? q = null, ReservationStatus? status = null) =>
            await new Hall(db, time, options, user).ListAsync(date, q, status, ct));

        hall.MapGet("/options", async (AppDbContext db, TimeProvider clock, StockOptions options, ClaimsPrincipal user, CancellationToken ct,
            string date, string time, int minutes, int guests, Guid? reservation = null) =>
            await new Hall(db, clock, options, user).OptionsAsync(date, time, minutes, guests, reservation, ct));

        hall.MapGet("/journal", async (AppDbContext db, CancellationToken ct, int take = 100) =>
            (await db.HallEvents.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct))
            .Select(e => new HallEventRow(e.Id, e.At, e.Actor, e.Entity, e.EntityId, e.Action, e.Text)));

        hall.MapPost("/reservations", (ReservationRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).CreateAsync(req, ct));
        hall.MapPut("/reservations/{id:guid}", (Guid id, ReservationRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).UpdateAsync(id, req, ct));
        hall.MapPost("/reservations/{id:guid}/cancel", (Guid id, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).CancelAsync(id, ct));
        hall.MapPost("/reservations/{id:guid}/no-show", (Guid id, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).NoShowAsync(id, ct));
        hall.MapPost("/reservations/{id:guid}/seat", (Guid id, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SeatReservationAsync(id, ct));
        hall.MapPost("/tables/{id:guid}/seat", (Guid id, SeatRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SeatWalkInAsync(id, req, ct));
        hall.MapPost("/visits/{id:guid}/finish", (Guid id, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).FinishAsync(id, ct));
        hall.MapPost("/visits/{id:guid}/extend", (Guid id, ExtendRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).ExtendAsync(id, req, ct));
        hall.MapPost("/tables/{id:guid}/ready", (Guid id, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).ReadyAsync(id, ct));

        hall.MapGet("/settings", async (AppDbContext db, CancellationToken ct) => ToDto(await Hall.SettingsAsync(db, ct)));

        // Схема, настройки и закрытие столов — только управляющий.
        var admin = hall.MapGroup("").RequireRoles(Access.HallAdmin);
        admin.MapPut("/settings", (HallSettingsDto req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SaveSettingsAsync(req, ct));
        admin.MapPost("/tables/{id:guid}/block", (Guid id, BlockRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).BlockAsync(id, req, ct));
        admin.MapPost("/blocks/{id:guid}/lift", (Guid id, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).LiftAsync(id, ct));
        admin.MapPost("/zones", (ZoneRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SaveZoneAsync(null, req, ct));
        admin.MapPut("/zones/{id:guid}", (Guid id, ZoneRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SaveZoneAsync(id, req, ct));
        admin.MapPost("/tables", (TableRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SaveTableAsync(null, req, ct));
        admin.MapPut("/tables/{id:guid}", (Guid id, TableRequest req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
            new Hall(db, time, options, user).SaveTableAsync(id, req, ct));
    }

    public static HallSettingsDto ToDto(HallSettings s) => new(s.VisitMinutes, s.BufferMinutes, s.NoShowMinutes, s.WarnMinutes);
}

/// <summary>Команды и срезы зала одного запроса: ресторан, часы и сотрудник уже известны.</summary>
internal sealed class Hall(AppDbContext db, TimeProvider clock, StockOptions options, ClaimsPrincipal user)
{
    private readonly DateTimeOffset _now = clock.GetUtcNow();
    private readonly TimeSpan _zone = TimeSpan.FromHours(options.UtcOffsetHours);
    private string Actor => user.Identity?.Name ?? "";

    private string T(DateTimeOffset t) => t.ToOffset(_zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    private string D(DateTimeOffset t) => t.ToOffset(_zone).ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    private DateTimeOffset Local(DateOnly date, TimeOnly time) => new DateTimeOffset(date.ToDateTime(time), _zone).ToUniversalTime();
    private DateOnly Today => DateOnly.FromDateTime(_now.ToOffset(_zone).DateTime);

    public static async Task<HallSettings> SettingsAsync(AppDbContext db, CancellationToken ct) =>
        await db.HallSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new HallSettings();

    private static string Guests(int n) => $"{n} {Ru.Plural(n, "гость", "гостя", "гостей")}";

    // ---------- Чтение ----------

    public async Task<IResult> ViewAsync(string? date, string? time, CancellationToken ct)
    {
        var live = string.IsNullOrEmpty(date) && string.IsNullOrEmpty(time);
        var at = _now;
        if (!live)
        {
            if (!DateOnly.TryParse(date ?? Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture, out var d)
                || !TimeOnly.TryParse(time ?? "12:00", System.Globalization.CultureInfo.InvariantCulture, out var t))
                return new Errors().Add("date", "Дата — ГГГГ-ММ-ДД, время — ЧЧ:ММ.").ToResult();
            at = Local(d, t);
        }
        var settings = await SettingsAsync(db, ct);
        var zones = await db.HallZones.AsNoTracking().Where(z => !z.Archived).OrderBy(z => z.Sort).ThenBy(z => z.Name).ToListAsync(ct);
        var tables = await db.HallTables.AsNoTracking().Where(t => !t.Archived).OrderBy(t => t.Number).ToListAsync(ct);
        var zoneNames = zones.ToDictionary(z => z.Id, z => z.Name);

        // День среза по часам ресторана; брони, идущие через полночь, попадают в оба дня.
        var localAt = at.ToOffset(_zone);
        var dayStart = new DateTimeOffset(localAt.Date, _zone).ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);
        var horizon = (at > _now ? at : _now).AddDays(-1);
        var reservations = await db.Reservations.AsNoTracking()
            .Where(r => (r.StartsAt < dayEnd && r.EndsAt > dayStart) || (r.Status == ReservationStatus.Confirmed && r.EndsAt > horizon && r.StartsAt < dayEnd.AddDays(1)))
            .OrderBy(r => r.StartsAt).ToListAsync(ct);
        var visits = await db.Visits.AsNoTracking().Where(v => v.EndedAt == null || v.ReadyAt == null).ToListAsync(ct);
        var visitByReservation = visits.Where(v => v.ReservationId != null).ToDictionary(v => v.ReservationId!.Value, v => v.Id);
        var blocks = await db.TableBlocks.AsNoTracking().Where(b => b.LiftedAt == null && (b.To == null || b.To > dayStart)).ToListAsync(ct);

        // Открытые заказы стола: официант оформляет их на место «Стол N».
        var orders = (await OrderEndpoints.OpenOrders(db, _now).AsNoTracking().Include(o => o.Items).ToListAsync(ct))
            .GroupBy(o => o.Place).ToDictionary(g => g.Key, g => g.OrderBy(o => o.CreatedAt)
                .Select(o => new TableOrder(o.Number, OrderEndpoints.Total(o), o.CreatedAt, o.ReadyAt != null)).ToList());

        var byTable = tables.ToDictionary(t => t.Id);
        ReservationView View(Reservation r) => ToView(r, byTable, zoneNames, settings, visitByReservation.TryGetValue(r.Id, out var v) ? v : null);

        var rows = tables.Select(t => Describe(t, at, live, settings,
                reservations.Where(r => r.TableId == t.Id).ToList(), visits.Where(v => v.TableId == t.Id).ToList(),
                blocks.Where(b => b.TableId == t.Id).ToList(), orders.GetValueOrDefault($"Стол {t.Number}") ?? [], View,
                reservations))
            .ToList();

        var day = reservations.Where(r => r.StartsAt < dayEnd && r.EndsAt > dayStart && byTable.ContainsKey(r.TableId)).Select(View).ToList();
        return Results.Ok(new HallView(_now, at, live, localAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), localAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), (int)_zone.TotalMinutes,
            HallEndpoints.ToDto(settings), zones.Select(z => new ZoneView(z.Id, z.Name, z.Sort)), rows, day, user.IsInRole(nameof(Role.Manager))));
    }

    /// <summary>Состояние стола на момент at: «недоступен», «занят», «уборка», «бронь» или «свободен до …».</summary>
    private TableView Describe(
        HallTable t, DateTimeOffset at, bool live, HallSettings s, List<Reservation> reservations, List<Visit> visits, List<TableBlock> blocks,
        List<TableOrder> orders, Func<Reservation, ReservationView> view, List<Reservation> all)
    {
        var buffer = TimeSpan.FromMinutes(s.BufferMinutes);
        var confirmed = reservations.Where(r => r.Status == ReservationStatus.Confirmed).OrderBy(r => r.StartsAt).ToList();
        var block = blocks.FirstOrDefault(b => b.From <= at && (b.To == null || at < b.To));
        var visit = visits.FirstOrDefault(v => v.EndedAt == null);
        var cleaning = visits.Where(v => v.EndedAt != null && v.ReadyAt == null).MaxBy(v => v.EndedAt);
        var current = confirmed.FirstOrDefault(r => r.StartsAt.AddMinutes(-s.WarnMinutes) <= at && at < r.EndsAt);
        var next = confirmed.FirstOrDefault(r => r.EndsAt > at);
        var upcoming = confirmed.Where(r => r.EndsAt > at).Take(4).Select(view).ToList();
        string? warning = null;
        DateTimeOffset? freeUntil = null;
        TableState state;
        string label;

        VisitView? visitView = null;
        if (visit is not null)
        {
            var guest = visit.ReservationId is { } rid ? all.FirstOrDefault(r => r.Id == rid)?.Guest : null;
            visitView = new VisitView(visit.Id, visit.TableId, visit.ReservationId, guest, visit.Guests, visit.SeatedAt, visit.ExpectedEndAt, null, visit.SeatedBy);
        }

        if (block is not null)
        {
            state = TableState.Blocked;
            label = block.To is { } to ? $"Недоступен до {D(to)}" : "Недоступен";
        }
        else if (visit is not null && (at <= _now || at < visit.ExpectedEndAt))
        {
            state = TableState.Occupied;
            // Ожидаемое время прошло, а гости сидят: стол остаётся занятым, пока визит не завершат.
            var late = (int)(_now - visit.ExpectedEndAt).TotalMinutes;
            label = late > 0 ? $"Занят · дольше на {late} мин" : $"Занят до {T(visit.ExpectedEndAt)}";
            var leaves = visit.ExpectedEndAt > _now ? visit.ExpectedEndAt : _now;
            if (confirmed.FirstOrDefault(r => r.StartsAt >= visit.SeatedAt) is { } after && leaves + buffer > after.StartsAt)
                warning = $"Бронь в {T(after.StartsAt)} ({after.Guest}) под угрозой: гости сидят до {T(leaves)}, нужен перерыв {s.BufferMinutes} мин.";
        }
        else if (cleaning is not null && at <= _now.AddMinutes(1))
        {
            state = TableState.Cleaning;
            label = $"Уборка с {T(cleaning.EndedAt!.Value)}";
            if (next is not null && next.StartsAt - _now <= TimeSpan.FromMinutes(s.WarnMinutes))
                warning = $"В {T(next.StartsAt)} бронь ({next.Guest}): подтвердите готовность стола.";
        }
        else if (current is not null)
        {
            state = TableState.Reserved;
            label = $"Бронь {T(current.StartsAt)} · {Guests(current.Guests)}";
            if (live && _now >= HallRules.NoShowFrom(current, s))
                warning = $"{current.Guest} опаздывает на {(int)(_now - current.StartsAt).TotalMinutes} мин: можно отметить неявку.";
        }
        else
        {
            state = TableState.Free;
            var blockAhead = blocks.Where(b => b.From > at).Select(b => (DateTimeOffset?)b.From).Min();
            var reservationAhead = next is null ? null : (DateTimeOffset?)(next.StartsAt - buffer);
            freeUntil = new[] { blockAhead, reservationAhead }.Where(x => x is not null).Min();
            label = freeUntil is { } until && until.ToOffset(_zone).Date == at.ToOffset(_zone).Date ? $"Свободен до {T(until)}" : "Свободен";
        }

        return new TableView(t.Id, t.ZoneId, t.Number, t.Seats, t.Shape, t.X, t.Y, t.W, t.H, t.Version, state, label, warning,
            visitView, next is null ? null : view(next),
            block is null ? null : new BlockView(block.Id, block.From, block.To, block.Reason, block.CreatedBy),
            state == TableState.Cleaning ? cleaning!.EndedAt : null, freeUntil, upcoming, orders);
    }

    private ReservationView ToView(Reservation r, IReadOnlyDictionary<Guid, HallTable> tables, IReadOnlyDictionary<Guid, string> zones, HallSettings s, Guid? visit)
    {
        var table = tables.GetValueOrDefault(r.TableId);
        return new ReservationView(r.Id, r.TableId, table?.Number ?? 0, table is null ? "" : zones.GetValueOrDefault(table.ZoneId) ?? "",
            r.Guest, r.Phone, r.Guests, r.StartsAt, r.EndsAt, r.Status, r.Note, r.Version, HallRules.NoShowFrom(r, s), visit);
    }

    private async Task<ReservationView> ToViewAsync(Reservation r, CancellationToken ct)
    {
        var tables = await db.HallTables.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var zones = await db.HallZones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name, ct);
        var visit = await db.Visits.AsNoTracking().Where(v => v.ReservationId == r.Id).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);
        return ToView(r, tables, zones, await SettingsAsync(db, ct), visit);
    }

    public async Task<IResult> ListAsync(string? date, string? q, ReservationStatus? status, CancellationToken ct)
    {
        var day = Today;
        if (!string.IsNullOrEmpty(date) && !DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out day))
            return new Errors().Add("date", "Дата — ГГГГ-ММ-ДД.").ToResult();
        var from = Local(day, TimeOnly.MinValue);
        var rows = await db.Reservations.AsNoTracking().Where(r => r.StartsAt >= from && r.StartsAt < from.AddDays(1))
            .Where(r => status == null || r.Status == status).OrderBy(r => r.StartsAt).ToListAsync(ct);
        // Имя и телефон в базе зашифрованы: поиск идёт по уже прочитанным броням дня.
        var text = (q ?? "").Trim();
        if (text != "")
            rows = rows.Where(r => r.Guest.Contains(text, StringComparison.OrdinalIgnoreCase) || (r.Phone ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        var tables = await db.HallTables.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var zones = await db.HallZones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name, ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var visits = await db.Visits.AsNoTracking().Where(v => v.ReservationId != null && ids.Contains(v.ReservationId.Value))
            .ToDictionaryAsync(v => v.ReservationId!.Value, v => v.Id, ct);
        var settings = await SettingsAsync(db, ct);
        return Results.Ok(rows.Select(r => ToView(r, tables, zones, settings, visits.TryGetValue(r.Id, out var v) ? v : null)));
    }

    /// <summary>Подбор стола для брони: все столы с отметкой, подходит ли стол, и причиной, если нет.</summary>
    public async Task<IResult> OptionsAsync(string date, string time, int minutes, int guests, Guid? except, CancellationToken ct)
    {
        if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out var d) || !TimeOnly.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out var t))
            return new Errors().Add("date", "Дата — ГГГГ-ММ-ДД, время — ЧЧ:ММ.").ToResult();
        var start = Local(d, t);
        var end = start.AddMinutes(Math.Clamp(minutes, 15, 720));
        var settings = await SettingsAsync(db, ct);
        var buffer = TimeSpan.FromMinutes(settings.BufferMinutes);
        var zones = await db.HallZones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z, ct);
        var tables = await db.HallTables.AsNoTracking().Where(x => !x.Archived).ToListAsync(ct);
        var slots = await SlotsAsync(tables.Select(x => x.Id).ToArray(), except, ct);

        var options = tables.Where(x => !zones[x.ZoneId].Archived).Select(x =>
        {
            string? reason = null;
            if (x.Seats < guests) reason = $"Мало мест: {x.Seats} из {guests}";
            else if (HallRules.Conflict(slots.GetValueOrDefault(x.Id) ?? [], start, end, buffer) is { } conflict) reason = Reason(conflict, settings);
            else if (end <= _now) reason = "Время уже прошло";
            return new TableOption(x.Id, x.Number, x.ZoneId, zones[x.ZoneId].Name, x.Seats, reason is null, reason);
        });
        // Сначала подходящие, из них — самые маленькие: большой стол остаётся для большой компании.
        return Results.Ok(options.OrderByDescending(o => o.Fits).ThenBy(o => o.Fits ? o.Seats : 0).ThenBy(o => o.Number).ToList());
    }

    private static string Reason(Slot conflict, HallSettings s) => conflict.Kind switch
    {
        SlotKind.Block => $"Стол закрыт: {conflict.Title}",
        SlotKind.Visit => $"Занят: {conflict.Title}",
        SlotKind.Cleaning => "Стол на уборке",
        _ => $"Пересекается с бронью {conflict.Title} (перерыв {s.BufferMinutes} мин)",
    };

    // ---------- Занятость и блокировка строк ----------

    /// <summary>
    /// Блокирует строки столов до конца транзакции. Порядок по идентификатору одинаков во всех командах:
    /// перенос с одного стола на другой не сцепится взаимно с переносом в обратную сторону.
    /// </summary>
    private async Task LockAsync(CancellationToken ct, params Guid[] tables)
    {
        var ids = tables.Distinct().Order().ToArray();
        await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" FROM "HallTables" WHERE "Id" = ANY({ids}) AND "RestaurantId" = {db.TenantId} ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
    }

    /// <summary>Закрепления столов: действующие брони, визиты, уборка и блокировки. Бронь except не учитывается (её переносят или по ней садят).</summary>
    private async Task<Dictionary<Guid, List<Slot>>> SlotsAsync(Guid[] tables, Guid? except, CancellationToken ct)
    {
        var since = _now.AddDays(-1);
        var reservations = await db.Reservations.AsNoTracking()
            .Where(r => tables.Contains(r.TableId) && r.Status == ReservationStatus.Confirmed && r.EndsAt > since && r.Id != except).ToListAsync(ct);
        var visits = await db.Visits.AsNoTracking().Where(v => tables.Contains(v.TableId) && (v.EndedAt == null || v.ReadyAt == null)).ToListAsync(ct);
        var blocks = await db.TableBlocks.AsNoTracking().Where(b => tables.Contains(b.TableId) && b.LiftedAt == null && (b.To == null || b.To > _now)).ToListAsync(ct);

        var slots = reservations.Select(r => (r.TableId, Slot: new Slot(SlotKind.Reservation, r.Id, r.StartsAt, r.EndsAt, $"{T(r.StartsAt)}–{T(r.EndsAt)}, {r.Guest}")))
            .Concat(visits.Where(v => v.EndedAt == null).Select(v => (v.TableId,
                // Гости, засидевшиеся дольше ожидаемого, занимают стол до сих пор.
                Slot: new Slot(SlotKind.Visit, v.Id, v.SeatedAt, v.ExpectedEndAt > _now ? v.ExpectedEndAt : _now, $"гости за столом до {T(v.ExpectedEndAt > _now ? v.ExpectedEndAt : _now)}"))))
            .Concat(visits.Where(v => v.EndedAt != null).Select(v => (v.TableId, Slot: new Slot(SlotKind.Cleaning, v.Id, v.EndedAt!.Value, _now, "уборка"))))
            .Concat(blocks.Select(b => (b.TableId, Slot: new Slot(SlotKind.Block, b.Id, b.From, b.To, b.To is { } to ? $"{b.Reason}, до {D(to)}" : b.Reason))));
        return slots.GroupBy(x => x.TableId).ToDictionary(g => g.Key, g => g.Select(x => x.Slot).ToList());
    }

    private static IResult Conflict(string text, Dictionary<string, object?>? extensions = null) =>
        Results.Problem(text, statusCode: StatusCodes.Status409Conflict, title: "Стол занят", extensions: extensions);

    private void Log(string entity, Guid? id, string action, string text) =>
        db.HallEvents.Add(new HallEvent { At = _now, Actor = Actor, Entity = entity, EntityId = id, Action = action, Text = text.Length > 500 ? text[..500] : text });

    // ---------- Брони ----------

    private Errors Validate(ReservationRequest req, HallTable? table, out DateTimeOffset start, out DateTimeOffset end)
    {
        var guest = (req.Guest ?? "").Trim();
        start = Local(req.Date, req.Time);
        end = start.AddMinutes(req.Minutes);
        var errors = new Errors()
            .Check(guest is { Length: > 0 and <= 80 }, "guest", "Укажите имя гостя — до 80 символов.")
            .Check((req.Phone ?? "").Trim().Length <= 30, "phone", "Контакт — до 30 символов.")
            .Check((req.Note ?? "").Trim().Length <= 300, "note", "Комментарий — до 300 символов.")
            .Check(req.Guests is > 0 and <= 50, "guests", "Гостей — от 1 до 50.")
            .Check(req.Minutes is >= 15 and <= 720, "minutes", "Длительность — от 15 минут до 12 часов.")
            .Check(table is not null, "tableId", "Такого стола нет.");
        if (table is not null && req.Guests > table.Seats)
            errors.Add("guests", $"За стол {table.Number} садится не больше {Guests(table.Seats)}.");
        if (end <= _now) errors.Add("time", "Время брони уже прошло.");
        return errors;
    }

    public async Task<IResult> CreateAsync(ReservationRequest req, CancellationToken ct)
    {
        // Повтор уже выполненного запроса: та же бронь, ничего нового.
        if (req.Id is { } known && await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == known, ct) is { } repeated)
            return Results.Created($"/api/hall/reservations/{repeated.Id}", await ToViewAsync(repeated, ct));

        var table = await db.HallTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == req.TableId && !t.Archived, ct);
        var errors = Validate(req, table, out var start, out var end);
        if (errors.Any) return errors.ToResult();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, table!.Id);
        // Второй из двух одинаковых запросов ждал блокировку и теперь видит бронь первого.
        if (req.Id is { } id && await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } twin)
            return Results.Created($"/api/hall/reservations/{twin.Id}", await ToViewAsync(twin, ct));

        var settings = await SettingsAsync(db, ct);
        var slots = (await SlotsAsync([table.Id], null, ct)).GetValueOrDefault(table.Id) ?? [];
        if (HallRules.Conflict(slots, start, end, TimeSpan.FromMinutes(settings.BufferMinutes)) is { } conflict)
            return Conflict($"Стол {table.Number}: {Reason(conflict, settings).ToLowerInvariant()}. Выберите другое время или стол.");

        var reservation = new Reservation
        {
            Id = req.Id ?? Guid.NewGuid(), TableId = table.Id, Guest = req.Guest!.Trim(), Phone = Blank(req.Phone), Guests = req.Guests,
            StartsAt = start, EndsAt = end, Status = ReservationStatus.Confirmed, Note = Blank(req.Note), CreatedAt = _now, CreatedBy = Actor,
        };
        db.Reservations.Add(reservation);
        Log("reservation", reservation.Id, "created", $"Бронь: {reservation.Guest}, {Guests(reservation.Guests)}, стол {table.Number}, {D(start)}–{T(end)}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/hall/reservations/{reservation.Id}", await ToViewAsync(reservation, ct));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Изменение и перенос брони одной транзакцией: оба стола заблокированы, новое время проверяется без самой брони.
    /// При конфликте ничего не меняется — бронь остаётся на прежнем столе и времени.
    /// </summary>
    public async Task<IResult> UpdateAsync(Guid id, ReservationRequest req, CancellationToken ct)
    {
        var current = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (current is null) return Results.NotFound();
        var table = await db.HallTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == req.TableId && !t.Archived, ct);
        var errors = Validate(req, table, out var start, out var end).Check(req.Version is not null, "version", "Укажите версию брони.");
        if (errors.Any) return errors.ToResult();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, current.TableId, table!.Id);
        var reservation = await db.Reservations.FirstAsync(r => r.Id == id, ct);
        if (reservation.Status != ReservationStatus.Confirmed)
            return Results.Problem("Изменить можно только подтверждённую бронь.", statusCode: StatusCodes.Status409Conflict);
        if (reservation.Version != req.Version)
            return Results.Problem("Бронь уже изменил другой сотрудник. Обновите список и повторите.", statusCode: StatusCodes.Status409Conflict);

        var settings = await SettingsAsync(db, ct);
        var slots = (await SlotsAsync([table.Id], id, ct)).GetValueOrDefault(table.Id) ?? [];
        if (HallRules.Conflict(slots, start, end, TimeSpan.FromMinutes(settings.BufferMinutes)) is { } conflict)
            return Conflict($"Стол {table.Number}: {Reason(conflict, settings).ToLowerInvariant()}. Бронь осталась прежней.");

        var oldTable = current.TableId == table.Id ? table : await db.HallTables.AsNoTracking().FirstAsync(t => t.Id == current.TableId, ct);
        var changes = new List<string>();
        if (oldTable.Id != table.Id) changes.Add($"стол {oldTable.Number} → {table.Number}");
        if (reservation.StartsAt != start || reservation.EndsAt != end) changes.Add($"{D(reservation.StartsAt)}–{T(reservation.EndsAt)} → {D(start)}–{T(end)}");
        if (reservation.Guests != req.Guests) changes.Add($"гостей {reservation.Guests} → {req.Guests}");
        if (reservation.Guest != req.Guest!.Trim()) changes.Add($"имя → {req.Guest.Trim()}");
        if (reservation.Phone != Blank(req.Phone) || reservation.Note != Blank(req.Note)) changes.Add("контакт или комментарий");

        (reservation.TableId, reservation.StartsAt, reservation.EndsAt, reservation.Guests, reservation.Guest, reservation.Phone, reservation.Note) =
            (table.Id, start, end, req.Guests, req.Guest.Trim(), Blank(req.Phone), Blank(req.Note));
        reservation.Version++;
        Log("reservation", id, oldTable.Id != table.Id || current.StartsAt != start ? "moved" : "changed",
            $"Бронь {reservation.Guest}: {(changes.Count == 0 ? "без изменений" : string.Join(", ", changes))}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(await ToViewAsync(reservation, ct));
    }

    public Task<IResult> CancelAsync(Guid id, CancellationToken ct) => MoveAsync(id, ReservationStatus.Cancelled, ct);

    public Task<IResult> NoShowAsync(Guid id, CancellationToken ct) => MoveAsync(id, ReservationStatus.NoShow, ct);

    private async Task<IResult> MoveAsync(Guid id, ReservationStatus to, CancellationToken ct)
    {
        var tableId = await db.Reservations.Where(r => r.Id == id).Select(r => (Guid?)r.TableId).FirstOrDefaultAsync(ct);
        if (tableId is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, tableId.Value);
        var reservation = await db.Reservations.FirstAsync(r => r.Id == id, ct);
        // Повтор того же действия (двойное нажатие) — не ошибка.
        if (reservation.Status == to) return Results.Ok(await ToViewAsync(reservation, ct));
        if (!HallRules.CanMove(reservation.Status, to))
            return Results.Problem($"Бронь уже {StatusText(reservation.Status)}: это действие недоступно.", statusCode: StatusCodes.Status409Conflict);

        var settings = await SettingsAsync(db, ct);
        if (to == ReservationStatus.NoShow && _now < HallRules.NoShowFrom(reservation, settings))
            return Results.Problem($"Неявку можно отметить с {T(HallRules.NoShowFrom(reservation, settings))}: гостя ждём {settings.NoShowMinutes} мин после начала брони.",
                statusCode: StatusCodes.Status409Conflict);

        reservation.Status = to;
        reservation.Version++;
        var number = await db.HallTables.Where(t => t.Id == reservation.TableId).Select(t => t.Number).FirstAsync(ct);
        Log("reservation", id, to == ReservationStatus.Cancelled ? "cancelled" : "no_show",
            $"{(to == ReservationStatus.Cancelled ? "Отмена брони" : "Неявка")}: {reservation.Guest}, стол {number}, {D(reservation.StartsAt)}–{T(reservation.EndsAt)}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(await ToViewAsync(reservation, ct));
    }

    private static string StatusText(ReservationStatus s) => s switch
    {
        ReservationStatus.Seated => "с гостями за столом",
        ReservationStatus.Completed => "завершена",
        ReservationStatus.Cancelled => "отменена",
        ReservationStatus.NoShow => "отмечена неявкой",
        _ => "подтверждена",
    };

    // ---------- Посадка, завершение, уборка ----------

    /// <summary>Можно ли посадить гостей за стол сейчас до момента until. Null — можно, иначе отказ.</summary>
    private async Task<IResult?> CheckSeatAsync(HallTable table, DateTimeOffset until, Guid? reservation, HallSettings settings, CancellationToken ct)
    {
        var slots = (await SlotsAsync([table.Id], reservation, ct)).GetValueOrDefault(table.Id) ?? [];
        if (slots.FirstOrDefault(s => s.Kind == SlotKind.Visit) is not null) return Conflict($"За столом {table.Number} уже сидят гости.");
        if (slots.FirstOrDefault(s => s.Kind == SlotKind.Cleaning) is not null) return Conflict($"Стол {table.Number} на уборке: сначала подтвердите, что он готов.");
        var buffer = TimeSpan.FromMinutes(settings.BufferMinutes);
        if (HallRules.Conflict(slots, _now, until, buffer) is not { } conflict) return null;
        if (conflict.Kind == SlotKind.Block) return Conflict($"Стол {table.Number} закрыт: {conflict.Title}.");
        // Свободного времени до следующей брони не хватает: сколько минут можно предложить гостям.
        var free = (int)Math.Floor((conflict.Start - buffer - _now).TotalMinutes);
        return Conflict(free >= 15
                ? $"Стол {table.Number}: в {T(conflict.Start)} бронь ({conflict.Title}). Посадить можно на {free} мин или выберите другой стол."
                : $"Стол {table.Number}: в {T(conflict.Start)} бронь ({conflict.Title}). Выберите другой стол.",
            new Dictionary<string, object?> { ["availableMinutes"] = Math.Max(0, free) });
    }

    /// <summary>Гости пришли по брони: та же бронь переходит в «за столом», визит создаётся по ней.</summary>
    public async Task<IResult> SeatReservationAsync(Guid id, CancellationToken ct)
    {
        var tableId = await db.Reservations.Where(r => r.Id == id).Select(r => (Guid?)r.TableId).FirstOrDefaultAsync(ct);
        if (tableId is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, tableId.Value);
        var reservation = await db.Reservations.FirstAsync(r => r.Id == id, ct);
        if (reservation.Status == ReservationStatus.Seated)
            return Results.Ok(ToVisit(await db.Visits.AsNoTracking().FirstAsync(v => v.ReservationId == id, ct), reservation.Guest));
        if (reservation.Status != ReservationStatus.Confirmed)
            return Results.Problem($"Бронь уже {StatusText(reservation.Status)}: посадить по ней нельзя.", statusCode: StatusCodes.Status409Conflict);

        var table = await db.HallTables.AsNoTracking().FirstAsync(t => t.Id == reservation.TableId, ct);
        var settings = await SettingsAsync(db, ct);
        // Пришли раньше — визит до конца брони; опоздали так, что бронь кончилась, — на ту же длительность от прихода.
        var until = reservation.EndsAt > _now ? reservation.EndsAt : _now + (reservation.EndsAt - reservation.StartsAt);
        if (await CheckSeatAsync(table, until, id, settings, ct) is { } refused) return refused;

        var visit = new Visit
        {
            Id = Guid.NewGuid(), TableId = table.Id, ReservationId = id, Guests = reservation.Guests,
            SeatedAt = _now, ExpectedEndAt = until, SeatedBy = Actor,
        };
        db.Visits.Add(visit);
        reservation.Status = ReservationStatus.Seated;
        reservation.Version++;
        Log("visit", visit.Id, "seated", $"Посадка по брони: {reservation.Guest}, {Guests(visit.Guests)}, стол {table.Number}, до {T(until)}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(ToVisit(visit, reservation.Guest));
    }

    /// <summary>Гости без брони: посадка учитывает следующую бронь стола и перерыв на подготовку.</summary>
    public async Task<IResult> SeatWalkInAsync(Guid tableId, SeatRequest req, CancellationToken ct)
    {
        if (req.Id is { } known && await db.Visits.AsNoTracking().FirstOrDefaultAsync(v => v.Id == known, ct) is { } repeated)
            return Results.Ok(ToVisit(repeated, null));
        var table = await db.HallTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tableId && !t.Archived, ct);
        if (table is null) return Results.NotFound();
        var settings = await SettingsAsync(db, ct);
        var minutes = req.Minutes ?? settings.VisitMinutes;
        var errors = new Errors()
            .Check(req.Guests > 0, "guests", "Гостей — хотя бы один.")
            .Check(req.Guests <= table.Seats, "guests", $"За стол {table.Number} садится не больше {Guests(table.Seats)}.")
            .Check(minutes is >= 15 and <= 720, "minutes", "Длительность — от 15 минут до 12 часов.");
        if (errors.Any) return errors.ToResult();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, table.Id);
        if (req.Id is { } id && await db.Visits.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct) is { } twin)
            return Results.Ok(ToVisit(twin, null));
        var until = _now.AddMinutes(minutes);
        if (await CheckSeatAsync(table, until, null, settings, ct) is { } refused) return refused;

        var visit = new Visit { Id = req.Id ?? Guid.NewGuid(), TableId = table.Id, Guests = req.Guests, SeatedAt = _now, ExpectedEndAt = until, SeatedBy = Actor };
        db.Visits.Add(visit);
        Log("visit", visit.Id, "seated", $"Посадка без брони: {Guests(visit.Guests)}, стол {table.Number}, до {T(until)}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(ToVisit(visit, null));
    }

    private static VisitView ToVisit(Visit v, string? guest) =>
        new(v.Id, v.TableId, v.ReservationId, guest, v.Guests, v.SeatedAt, v.ExpectedEndAt, v.EndedAt, v.SeatedBy);

    /// <summary>Гости ушли: визит завершён, стол — на уборке. Оплату это не означает: счёт закрывается в заказах.</summary>
    public async Task<IResult> FinishAsync(Guid id, CancellationToken ct)
    {
        var tableId = await db.Visits.Where(v => v.Id == id).Select(v => (Guid?)v.TableId).FirstOrDefaultAsync(ct);
        if (tableId is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, tableId.Value);
        var visit = await db.Visits.FirstAsync(v => v.Id == id, ct);
        if (visit.EndedAt is not null) return Results.Ok(ToVisit(visit, null));
        (visit.EndedAt, visit.EndedBy) = (_now, Actor);
        if (visit.ReservationId is { } rid)
        {
            var reservation = await db.Reservations.FirstAsync(r => r.Id == rid, ct);
            reservation.Status = ReservationStatus.Completed;
            reservation.Version++;
        }
        var number = await db.HallTables.Where(t => t.Id == visit.TableId).Select(t => t.Number).FirstAsync(ct);
        Log("visit", id, "finished", $"Гости ушли: стол {number} на уборке (сидели с {T(visit.SeatedAt)})");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(ToVisit(visit, null));
    }

    /// <summary>Гости задерживаются: ожидаемый уход сдвигается. Риск для следующей брони показывает срез зала.</summary>
    public async Task<IResult> ExtendAsync(Guid id, ExtendRequest req, CancellationToken ct)
    {
        if (req.Minutes is < 5 or > 240) return new Errors().Add("minutes", "Продлить можно на 5–240 минут.").ToResult();
        var tableId = await db.Visits.Where(v => v.Id == id).Select(v => (Guid?)v.TableId).FirstOrDefaultAsync(ct);
        if (tableId is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, tableId.Value);
        var visit = await db.Visits.FirstAsync(v => v.Id == id, ct);
        if (visit.EndedAt is not null) return Results.Problem("Визит уже завершён.", statusCode: StatusCodes.Status409Conflict);
        var from = visit.ExpectedEndAt > _now ? visit.ExpectedEndAt : _now;
        visit.ExpectedEndAt = from.AddMinutes(req.Minutes);
        var number = await db.HallTables.Where(t => t.Id == visit.TableId).Select(t => t.Number).FirstAsync(ct);
        Log("visit", id, "extended", $"Визит за столом {number} продлён до {T(visit.ExpectedEndAt)}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(ToVisit(visit, null));
    }

    /// <summary>Стол убран и накрыт: снова свободен (или ждёт следующую бронь). По таймеру сам не освобождается.</summary>
    public async Task<IResult> ReadyAsync(Guid tableId, CancellationToken ct)
    {
        var table = await db.HallTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tableId, ct);
        if (table is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, tableId);
        var cleaning = await db.Visits.Where(v => v.TableId == tableId && v.EndedAt != null && v.ReadyAt == null).ToListAsync(ct);
        if (cleaning.Count == 0) return Results.NoContent();
        foreach (var v in cleaning) (v.ReadyAt, v.ReadyBy) = (_now, Actor);
        Log("table", tableId, "ready", $"Стол {table.Number} убран и готов");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    // ---------- Блокировка стола ----------

    /// <summary>
    /// Закрыть стол на период. Поверх визита закрыть нельзя. Если на период есть брони, они не теряются молча:
    /// без явного согласия на их отмену запрос отклоняется со списком броней — их переносят на другие столы.
    /// </summary>
    public async Task<IResult> BlockAsync(Guid tableId, BlockRequest req, CancellationToken ct)
    {
        var table = await db.HallTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tableId && !t.Archived, ct);
        if (table is null) return Results.NotFound();
        var reason = (req.Reason ?? "").Trim();
        var from = req.Date is { } d ? Local(d, req.Time ?? TimeOnly.MinValue) : _now;
        if (from < _now) from = _now;
        DateTimeOffset? to = req.UntilDate is { } u ? Local(u, req.UntilTime ?? TimeOnly.MinValue) : null;
        var errors = new Errors()
            .Check(reason is { Length: > 0 and <= 200 }, "reason", "Укажите причину — до 200 символов.")
            .Check(to is null || to > from, "until", "Конец блокировки должен быть позже начала.");
        if (errors.Any) return errors.ToResult();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct, tableId);
        if (from <= _now.AddMinutes(1) && await db.Visits.AnyAsync(v => v.TableId == tableId && v.EndedAt == null, ct))
            return Conflict($"За столом {table.Number} сидят гости: сначала завершите визит.");

        var affected = await db.Reservations.Where(r => r.TableId == tableId && r.Status == ReservationStatus.Confirmed && r.EndsAt > from && (to == null || r.StartsAt < to))
            .OrderBy(r => r.StartsAt).ToListAsync(ct);
        if (affected.Count > 0 && !req.CancelReservations)
        {
            var views = new List<ReservationView>();
            foreach (var r in affected) views.Add(await ToViewAsync(r, ct));
            return Conflict($"На это время у стола {table.Number} есть брони: {string.Join("; ", affected.Select(r => $"{D(r.StartsAt)} {r.Guest}"))}. Перенесите их на другие столы или подтвердите отмену.",
                new Dictionary<string, object?> { ["reservations"] = views });
        }
        foreach (var r in affected)
        {
            r.Status = ReservationStatus.Cancelled;
            r.Version++;
            Log("reservation", r.Id, "cancelled", $"Отмена брони при закрытии стола {table.Number}: {r.Guest}, {D(r.StartsAt)}–{T(r.EndsAt)}");
        }
        var block = new TableBlock { Id = Guid.NewGuid(), TableId = tableId, From = from, To = to, Reason = reason, CreatedAt = _now, CreatedBy = Actor };
        db.TableBlocks.Add(block);
        Log("block", block.Id, "blocked", $"Стол {table.Number} закрыт с {D(from)}{(to is { } t ? $" до {D(t)}" : "")}: {reason}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new BlockView(block.Id, block.From, block.To, block.Reason, block.CreatedBy));
    }

    public async Task<IResult> LiftAsync(Guid id, CancellationToken ct)
    {
        var block = await db.TableBlocks.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (block is null) return Results.NotFound();
        if (block.LiftedAt is null)
        {
            (block.LiftedAt, block.LiftedBy) = (_now, Actor);
            var number = await db.HallTables.Where(t => t.Id == block.TableId).Select(t => t.Number).FirstAsync(ct);
            Log("block", id, "lifted", $"Стол {number} снова доступен ({block.Reason})");
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    // ---------- Схема и настройки ----------

    public async Task<IResult> SaveSettingsAsync(HallSettingsDto req, CancellationToken ct)
    {
        var errors = new Errors()
            .Check(req.VisitMinutes is >= 15 and <= 720, "visitMinutes", "Длительность визита — от 15 минут до 12 часов.")
            .Check(req.BufferMinutes is >= 0 and <= 120, "bufferMinutes", "Перерыв между гостями — от 0 до 120 минут.")
            .Check(req.NoShowMinutes is >= 0 and <= 120, "noShowMinutes", "Ожидание гостя — от 0 до 120 минут.")
            .Check(req.WarnMinutes is >= 0 and <= 240, "warnMinutes", "Показ брони заранее — от 0 до 240 минут.");
        if (errors.Any) return errors.ToResult();
        var row = await db.HallSettings.FirstOrDefaultAsync(ct);
        if (row is null) db.HallSettings.Add(row = new HallSettings { RestaurantId = db.TenantId });
        (row.VisitMinutes, row.BufferMinutes, row.NoShowMinutes, row.WarnMinutes) = (req.VisitMinutes, req.BufferMinutes, req.NoShowMinutes, req.WarnMinutes);
        Log("settings", null, "changed", $"Настройки зала: визит {req.VisitMinutes} мин, перерыв {req.BufferMinutes} мин, ожидание {req.NoShowMinutes} мин, бронь видна за {req.WarnMinutes} мин");
        await db.SaveChangesAsync(ct);
        return Results.Ok(HallEndpoints.ToDto(row));
    }

    public async Task<IResult> SaveZoneAsync(Guid? id, ZoneRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        var errors = new Errors()
            .Check(name is { Length: > 0 and <= 60 }, "name", "Укажите название зоны — до 60 символов.")
            .Check(req.Sort is >= 0 and <= 1000, "sort", "Порядок — от 0 до 1000.");
        if (errors.Any) return errors.ToResult();
        var zone = id is null ? null : await db.HallZones.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (id is not null && zone is null) return Results.NotFound();
        if (zone is null)
        {
            db.HallZones.Add(zone = new HallZone { Id = Guid.NewGuid(), Name = name });
            Log("zone", zone.Id, "created", $"Зона «{name}»");
        }
        else
        {
            if (req.Archived && !zone.Archived && await db.HallTables.AnyAsync(t => t.ZoneId == zone.Id && !t.Archived, ct))
                return Results.Problem("В зоне есть столы: сначала уберите их в архив или перенесите в другую зону.", statusCode: StatusCodes.Status409Conflict);
            Log("zone", zone.Id, req.Archived && !zone.Archived ? "archived" : "changed", $"Зона «{zone.Name}»{(zone.Name != name ? $" → «{name}»" : "")}{(req.Archived && !zone.Archived ? ": в архиве" : "")}");
            zone.Name = name;
        }
        (zone.Sort, zone.Archived) = (req.Sort, req.Archived);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ZoneView(zone.Id, zone.Name, zone.Sort));
    }

    public async Task<IResult> SaveTableAsync(Guid? id, TableRequest req, CancellationToken ct)
    {
        var errors = new Errors()
            .Check(req.Number is >= 1 and <= HallEndpoints.MaxTableNumber, "number", $"Номер стола — от 1 до {HallEndpoints.MaxTableNumber}.")
            .Check(req.Seats is >= 1 and <= 30, "seats", "Мест — от 1 до 30.")
            .Check(Enum.IsDefined(req.Shape), "shape", "Форма — круглый или прямоугольный.")
            .Check(req.X is >= 0 and <= 100 && req.Y is >= 0 and <= 100, "x", "Положение — от 0 до 100% зала.")
            .Check(req.W is >= 3 and <= 50 && req.H is >= 3 and <= 50, "w", "Размер — от 3 до 50% зала.")
            .Check(id is null || req.Version is not null, "version", "Укажите версию стола.");
        var zone = await db.HallZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == req.ZoneId && !z.Archived, ct);
        errors.Check(zone is not null, "zoneId", "Такой зоны нет.");
        if (errors.Any) return errors.ToResult();
        if (!req.Archived && await db.HallTables.AnyAsync(t => t.Number == req.Number && !t.Archived && t.Id != id, ct))
            return errors.Add("number", $"Стол с номером {req.Number} уже есть.").ToResult();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        HallTable table;
        if (id is null)
        {
            table = new HallTable { Id = Guid.NewGuid() };
            db.HallTables.Add(table);
            Log("table", table.Id, "created", $"Стол {req.Number}: {req.Seats} {Ru.Plural(req.Seats, "место", "места", "мест")}, зона «{zone!.Name}»");
        }
        else
        {
            await LockAsync(ct, id.Value);
            var found = await db.HallTables.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (found is null) return Results.NotFound();
            table = found;
            if (table.Version != req.Version)
                return Results.Problem("Стол уже изменил другой сотрудник. Обновите схему и повторите.", statusCode: StatusCodes.Status409Conflict);
            // Будущие подтверждённые брони не должны оказаться за столом, где им не хватает мест или которого нет.
            var future = await db.Reservations.AsNoTracking()
                .Where(r => r.TableId == table.Id && r.Status == ReservationStatus.Confirmed && r.EndsAt > _now).OrderBy(r => r.StartsAt).ToListAsync(ct);
            var crowded = future.Where(r => req.Archived || r.Guests > req.Seats).ToList();
            if (crowded.Count > 0)
                return Results.Problem($"Сначала перенесите брони: {string.Join("; ", crowded.Select(r => $"{D(r.StartsAt)} {r.Guest}, {Guests(r.Guests)}"))}.",
                    statusCode: StatusCodes.Status409Conflict);
            if (req.Archived && await db.Visits.AnyAsync(v => v.TableId == table.Id && v.EndedAt == null, ct))
                return Results.Problem("За столом сидят гости: сначала завершите визит.", statusCode: StatusCodes.Status409Conflict);
            var changes = new List<string>();
            if (table.Number != req.Number) changes.Add($"номер {table.Number} → {req.Number}");
            if (table.Seats != req.Seats) changes.Add($"мест {table.Seats} → {req.Seats}");
            if (table.ZoneId != req.ZoneId) changes.Add($"зона → «{zone!.Name}»");
            if ((table.X, table.Y, table.W, table.H, table.Shape) != (req.X, req.Y, req.W, req.H, req.Shape)) changes.Add("положение или форма");
            if (req.Archived && !table.Archived) changes.Add("в архиве");
            Log("table", table.Id, req.Archived && !table.Archived ? "archived" : "changed", $"Стол {table.Number}: {(changes.Count == 0 ? "без изменений" : string.Join(", ", changes))}");
            table.Version++;
        }
        (table.ZoneId, table.Number, table.Seats, table.Shape, table.X, table.Y, table.W, table.H, table.Archived) =
            (req.ZoneId, req.Number, req.Seats, req.Shape, req.X, req.Y, req.W, req.H, req.Archived);

        // Номер стола — место заказа «Стол N»: число столов ресторана растёт, чтобы официант мог оформить заказ на этот стол.
        var restaurant = await db.Restaurants.FirstAsync(r => r.Id == db.TenantId, ct);
        if (!req.Archived && req.Number > restaurant.Tables) restaurant.Tables = req.Number;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return id is null
            ? Results.Created($"/api/hall/tables/{table.Id}", new { table.Id, table.Number, table.Version })
            : Results.Ok(new { table.Id, table.Number, table.Version });
    }
}
