using Ostatok.Api.Domain;

namespace Ostatok.Tests;

/// <summary>Правила занятости стола без базы: пересечение интервалов, перерыв между гостями, переходы брони.</summary>
public class HallDomainTests
{
    private static readonly DateTimeOffset Six = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);   // 18:00 по Москве
    private static readonly TimeSpan Buffer = TimeSpan.FromMinutes(15);

    private static Slot Booked(DateTimeOffset start, int minutes) => new(SlotKind.Reservation, Guid.NewGuid(), start, start.AddMinutes(minutes), "бронь");

    [Fact]
    public void Соседняя_бронь_разрешена_только_после_перерыва()
    {
        var slots = new[] { Booked(Six, 120) };   // 18:00–20:00

        Assert.Null(HallRules.Conflict(slots, Six.AddMinutes(135), Six.AddMinutes(255), Buffer));        // с 20:15 — можно
        Assert.NotNull(HallRules.Conflict(slots, Six.AddMinutes(120), Six.AddMinutes(240), Buffer));     // с 20:00 — мешает перерыв
        Assert.NotNull(HallRules.Conflict(slots, Six.AddMinutes(60), Six.AddMinutes(180), Buffer));      // пересечение
        Assert.Null(HallRules.Conflict(slots, Six.AddMinutes(-120), Six.AddMinutes(-15), Buffer));       // 16:00–17:45 — можно
        Assert.NotNull(HallRules.Conflict(slots, Six.AddMinutes(-120), Six.AddMinutes(-10), Buffer));    // 16:00–17:50 — перерыв меньше 15 минут
    }

    [Fact]
    public void Блокировка_без_перерыва_а_бессрочная_закрывает_всё_будущее()
    {
        var block = new Slot(SlotKind.Block, Guid.NewGuid(), Six, Six.AddMinutes(120), "ремонт");
        Assert.Null(HallRules.Conflict([block], Six.AddMinutes(120), Six.AddMinutes(180), Buffer));
        Assert.NotNull(HallRules.Conflict([block], Six.AddMinutes(119), Six.AddMinutes(180), Buffer));

        var forever = block with { End = null };
        Assert.NotNull(HallRules.Conflict([forever], Six.AddDays(30), Six.AddDays(30).AddHours(2), Buffer));
        Assert.Null(HallRules.Conflict([forever], Six.AddHours(-3), Six.AddHours(-1), Buffer));
    }

    [Fact]
    public void Первым_называется_самое_раннее_закрепление()
    {
        var later = Booked(Six.AddHours(2), 60);
        var earlier = Booked(Six, 60);
        Assert.Equal(earlier, HallRules.Conflict([later, earlier], Six.AddMinutes(-30), Six.AddHours(4), Buffer));
    }

    [Fact]
    public void Переходы_брони_только_из_допустимых_состояний()
    {
        Assert.True(HallRules.CanMove(ReservationStatus.Confirmed, ReservationStatus.Seated));
        Assert.True(HallRules.CanMove(ReservationStatus.Confirmed, ReservationStatus.Cancelled));
        Assert.True(HallRules.CanMove(ReservationStatus.Confirmed, ReservationStatus.NoShow));
        Assert.True(HallRules.CanMove(ReservationStatus.Seated, ReservationStatus.Completed));
        Assert.False(HallRules.CanMove(ReservationStatus.Seated, ReservationStatus.Cancelled));
        Assert.False(HallRules.CanMove(ReservationStatus.Cancelled, ReservationStatus.Seated));
        Assert.False(HallRules.CanMove(ReservationStatus.NoShow, ReservationStatus.Confirmed));
        Assert.False(HallRules.CanMove(ReservationStatus.Completed, ReservationStatus.Seated));
    }

    [Fact]
    public void Неявка_после_ожидания_от_начала_брони()
    {
        var r = new Reservation { Guest = "Гость", StartsAt = Six, EndsAt = Six.AddHours(2) };
        Assert.Equal(Six.AddMinutes(15), HallRules.NoShowFrom(r, new HallSettings()));
        Assert.Equal(Six.AddMinutes(30), HallRules.NoShowFrom(r, new HallSettings { NoShowMinutes = 30 }));
    }
}
