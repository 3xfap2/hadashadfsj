namespace Ostatok.Api.Domain;

/// <summary>Зона зала: «Основной зал», «Терраса», «VIP». Архивная зона не показывается, её история остаётся.</summary>
public class HallZone : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int Sort { get; set; }
    public bool Archived { get; set; }
}

public enum TableShape { Round, Rect }

/// <summary>
/// Столик на схеме. Своего статуса у него нет: «занят», «бронь», «уборка» вычисляются из визитов, броней и блокировок.
/// Номер стола — тот же, что у заказа («Стол 5»): официант выбирает стол из схемы.
/// </summary>
public class HallTable : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public Guid ZoneId { get; set; }
    public int Number { get; set; }
    public int Seats { get; set; }
    public TableShape Shape { get; set; }
    /// <summary>Положение и размер на схеме — в процентах ширины и высоты зала.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Archived { get; set; }
    /// <summary>Растёт при каждом изменении: устаревшая форма получает 409, а не затирает чужую правку.</summary>
    public int Version { get; set; }
}

public enum ReservationStatus { Confirmed, Seated, Completed, Cancelled, NoShow }

/// <summary>Бронь стола на интервал [StartsAt, EndsAt). Время — абсолютное (UTC), дата и часы ресторана считаются при показе.</summary>
public class Reservation : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public Guid TableId { get; set; }
    /// <summary>Имя гостя и контакт — личные данные: в базе зашифрованы.</summary>
    public required string Guest { get; set; }
    public string? Phone { get; set; }
    public int Guests { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public ReservationStatus Status { get; set; }
    public string? Note { get; set; }
    /// <summary>Откуда бронь: «зал» (сотрудник) — пока единственный источник; онлайн-бронирование — следующий этап.</summary>
    public string Source { get; set; } = "зал";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public int Version { get; set; }
}

/// <summary>Фактический визит: гости сели за стол. Завершение визита переводит стол на уборку, готовность — освобождает.</summary>
public class Visit : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public Guid TableId { get; set; }
    public Guid? ReservationId { get; set; }
    public int Guests { get; set; }
    public DateTimeOffset SeatedAt { get; set; }
    /// <summary>Ожидаемый уход. Прошедшее время не освобождает стол: гости уходят, когда сотрудник завершит визит.</summary>
    public DateTimeOffset ExpectedEndAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Когда стол после визита подтвердили готовым; пусто после завершения — стол на уборке.</summary>
    public DateTimeOffset? ReadyAt { get; set; }
    public string SeatedBy { get; set; } = "";
    public string? EndedBy { get; set; }
    public string? ReadyBy { get; set; }
}

/// <summary>Стол закрыт управляющим на период [From, To); To пусто — до снятия блокировки.</summary>
public class TableBlock : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public Guid TableId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset? To { get; set; }
    public required string Reason { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }
    public string? LiftedBy { get; set; }
}

/// <summary>Настройки зала ресторана; нет строки — действуют значения по умолчанию.</summary>
public class HallSettings : IOwned
{
    public Guid RestaurantId { get; set; }
    /// <summary>Длительность визита и брони по умолчанию.</summary>
    public int VisitMinutes { get; set; } = 120;
    /// <summary>Перерыв между визитами на одном столе: убрать и накрыть.</summary>
    public int BufferMinutes { get; set; } = 15;
    /// <summary>Сколько ждать гостя после начала брони, прежде чем можно отметить неявку.</summary>
    public int NoShowMinutes { get; set; } = 15;
    /// <summary>За сколько минут до брони стол показывается как «бронь».</summary>
    public int WarnMinutes { get; set; } = 30;
}

/// <summary>Журнал зала (AuditEvent): кто, когда и что сделал со столами, бронями и визитами.</summary>
public class HallEvent : IOwned
{
    public Guid RestaurantId { get; set; }
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    /// <summary>table, zone, reservation, visit, block, settings.</summary>
    public required string Entity { get; set; }
    public Guid? EntityId { get; set; }
    /// <summary>Действие кодом: created, changed, cancelled, seated, finished, ready, blocked…</summary>
    public required string Action { get; set; }
    /// <summary>Что произошло словами, с изменёнными значениями.</summary>
    public required string Text { get; set; }
}

public enum TableState { Free, Reserved, Occupied, Cleaning, Blocked }

public enum SlotKind { Reservation, Visit, Cleaning, Block }

/// <summary>Чем занят стол на интервал [Start, End); End пусто — без конца (бессрочная блокировка).</summary>
public sealed record Slot(SlotKind Kind, Guid Id, DateTimeOffset Start, DateTimeOffset? End, string Title);

/// <summary>Правила занятости стола: без базы, одинаковые для брони, переноса и посадки.</summary>
public static class HallRules
{
    /// <summary>
    /// Первое закрепление, с которым пересекается интервал [start, end) с учётом перерыва между визитами.
    /// Интервалы полуоткрытые: бронь 18:00–20:00 при перерыве 15 минут допускает следующую с 20:15, но не с 20:00.
    /// Перерыв нужен между гостями; у блокировки его нет.
    /// </summary>
    public static Slot? Conflict(IEnumerable<Slot> slots, DateTimeOffset start, DateTimeOffset end, TimeSpan buffer) =>
        slots.Where(s => Overlaps(s, start, end, s.Kind == SlotKind.Block ? TimeSpan.Zero : buffer)).OrderBy(s => s.Start).FirstOrDefault();

    public static bool Overlaps(Slot slot, DateTimeOffset start, DateTimeOffset end, TimeSpan buffer) =>
        (slot.End is not { } slotEnd || start < slotEnd + buffer) && slot.Start < end + buffer;

    /// <summary>Неявку можно отметить, когда гость опоздал больше разрешённого ожидания.</summary>
    public static DateTimeOffset NoShowFrom(Reservation r, HallSettings settings) => r.StartsAt.AddMinutes(settings.NoShowMinutes);

    /// <summary>Переход брони в новый статус: что разрешено из какого состояния.</summary>
    public static bool CanMove(ReservationStatus from, ReservationStatus to) => (from, to) switch
    {
        (ReservationStatus.Confirmed, ReservationStatus.Seated or ReservationStatus.Cancelled or ReservationStatus.NoShow) => true,
        (ReservationStatus.Seated, ReservationStatus.Completed) => true,
        _ => false,
    };
}
