namespace Ostatok.Api.Domain;

/// <param name="StopHours">Сколько рабочих часов блюдо простояло в стопе.</param>
/// <param name="PortionsPerHour">Сколько порций в час блюдо продаёт, когда оно в продаже.</param>
public sealed record StopCostRow(
    Guid DishId, string DishName, double StopHours, decimal PortionsPerHour, decimal LostPortions, decimal LostRevenue);

/// <summary>
/// Цена стопа: сколько выручки блюдо не принесло, пока стояло в стоп-листе.
///   потерянные порции = рабочие часы в стопе × (продано порций ÷ рабочие часы в продаже)
/// Считаются только часы смены: ночью блюдо не продаётся, в стопе оно или нет.
/// </summary>
public static class StopCost
{
    public static StopCostRow Of(
        Dish dish, IEnumerable<(DateTimeOffset From, DateTimeOffset To)> stops, int soldPortions,
        DateTimeOffset since, DateTimeOffset now, TimeSpan shiftStart, TimeSpan shiftEnd, TimeSpan zone)
    {
        var stopHours = stops.Sum(s => OpenHours(s.From > since ? s.From : since, s.To, shiftStart, shiftEnd, zone));
        var onSaleHours = OpenHours(since, now, shiftStart, shiftEnd, zone) - stopHours;
        // Блюдо, которое за период ни разу не продали, оценить нечем: его спрос неизвестен.
        var perHour = onSaleHours > 0 ? soldPortions / (decimal)onSaleHours : 0;
        var lost = perHour * (decimal)stopHours;
        return new StopCostRow(dish.Id, dish.Name, Math.Round(stopHours, 1), Math.Round(perHour, 2), Math.Round(lost, 1), Math.Round(lost * dish.Price));
    }

    /// <summary>Сколько часов интервала [from, to] приходится на смену (время смены — по часам ресторана).</summary>
    public static double OpenHours(DateTimeOffset from, DateTimeOffset to, TimeSpan shiftStart, TimeSpan shiftEnd, TimeSpan zone)
    {
        if (to <= from) return 0;
        var hours = 0.0;
        for (var day = from.ToOffset(zone).Date; day <= to.ToOffset(zone).Date; day = day.AddDays(1))
        {
            var open = new DateTimeOffset(day + shiftStart, zone);
            var close = new DateTimeOffset(day + shiftEnd, zone);
            var start = open > from ? open : from;
            var end = close < to ? close : to;
            if (end > start) hours += (end - start).TotalHours;
        }
        return hours;
    }

    /// <summary>Разбирает время смены вида «12:00»; «24:00» — полночь следующего дня.</summary>
    public static TimeSpan ParseTime(string value)
    {
        var parts = value.Split(':');
        return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0);
    }
}
