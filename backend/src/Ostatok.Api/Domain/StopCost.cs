namespace Ostatok.Api.Domain;

/// <param name="StopHours">Сколько рабочих часов блюдо простояло в стопе.</param>
/// <param name="PortionsPerHour">Сколько порций в час блюдо продаёт, когда оно в продаже.</param>
public sealed record StopCostRow(
    Guid DishId, string DishName, double StopHours, decimal PortionsPerHour, decimal LostPortions, decimal LostRevenue);

/// <summary>
/// Цена стопа: сколько выручки блюдо не принесло, пока стояло в стоп-листе.
///   потерянные порции = рабочие часы в стопе × (продано порций ÷ рабочие часы в продаже)
/// Считаются только часы смены: ночью блюдо не продаётся, в стопе оно или нет.
/// Смена может переходить через полночь (18:00–02:00): тогда её конец — на следующий день.
/// </summary>
public static class StopCost
{
    /// <param name="from">
    /// Отчёт за смену: скорость продаж считается за весь период [since, now], а простой и потери — только начиная с from.
    /// Не указан — за весь период.
    /// </param>
    public static StopCostRow Of(
        Dish dish, IEnumerable<(DateTimeOffset From, DateTimeOffset To)> stops, int soldPortions,
        DateTimeOffset since, DateTimeOffset now, TimeSpan shiftStart, TimeSpan shiftEnd, TimeSpan zone, DateTimeOffset? from = null)
    {
        double StopHours(DateTimeOffset start) => stops.Sum(s => OpenHours(s.From > start ? s.From : start, s.To, shiftStart, shiftEnd, zone));

        var onSaleHours = OpenHours(since, now, shiftStart, shiftEnd, zone) - StopHours(since);
        // Блюдо, которое за период ни разу не продали, оценить нечем: его спрос неизвестен.
        var perHour = onSaleHours > 0 ? soldPortions / (decimal)onSaleHours : 0;
        var stopHours = StopHours(from ?? since);
        var lost = perHour * (decimal)stopHours;
        return new StopCostRow(dish.Id, dish.Name, Math.Round(stopHours, 1), Math.Round(perHour, 2), Math.Round(lost, 1), Math.Round(lost * dish.Price));
    }

    /// <summary>Сколько часов интервала [from, to] приходится на смену (время смены — по часам ресторана).</summary>
    public static double OpenHours(DateTimeOffset from, DateTimeOffset to, TimeSpan shiftStart, TimeSpan shiftEnd, TimeSpan zone)
    {
        if (to <= from) return 0;
        var hours = 0.0;
        // Отсчёт — со вчерашней смены: если она через полночь, её хвост попадает в начало интервала.
        for (var day = from.ToOffset(zone).Date.AddDays(-1); day <= to.ToOffset(zone).Date; day = day.AddDays(1))
        {
            var open = new DateTimeOffset(day + shiftStart, zone);
            var close = new DateTimeOffset(day + shiftEnd, zone);
            var start = open > from ? open : from;
            var end = close < to ? close : to;
            if (end > start) hours += (end - start).TotalHours;
        }
        return hours;
    }

    /// <summary>
    /// Часы смены от полуночи дня, в который она началась. Конец не позже начала значит, что смена
    /// заканчивается на следующий день: «18:00–02:00» — это с 18:00 до 26:00.
    /// </summary>
    public static (TimeSpan Start, TimeSpan End) Shift(string start, string end)
    {
        var (from, to) = (ParseTime(start), ParseTime(end));
        return (from, to <= from ? to + TimeSpan.FromDays(1) : to);
    }

    /// <summary>Разбирает время смены вида «12:00» или «9:00»; «24:00» — полночь следующего дня.</summary>
    public static TimeSpan ParseTime(string value)
    {
        var parts = value.Split(':');
        return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0);
    }
}
