namespace Ostatok.Api.Domain;

/// <summary>Расход продукта за один день по времени ресторана.</summary>
public sealed record DailyUse(Guid ProductId, DateOnly Day, decimal Quantity);

/// <summary>
/// Прогноз расхода с учётом дня недели: в пятницу продукта уходит больше, чем в понедельник,
/// и средний расход за неделю в обе стороны ошибается.
/// </summary>
public static class Demand
{
    /// <summary>
    /// Ожидаемый расход в час на ближайшие horizonDays дней, начиная с сегодняшнего.
    /// Для каждого из этих дней берётся средний расход в такие же дни недели из истории;
    /// если такого дня недели в истории ещё не было — средний по всем дням.
    /// История каждого продукта начинается с первого дня, когда он расходовался: дни до появления
    /// нового блюда в меню — не нулевой спрос, а отсутствие наблюдений.
    /// Сегодняшний день в историю не входит: он ещё не закончился.
    /// Пустой результат означает, что полных дней истории нет.
    /// </summary>
    public static Dictionary<Guid, decimal> PerHour(IReadOnlyCollection<DailyUse> history, DateOnly today, int horizonDays)
    {
        var past = history.Where(u => u.Day < today).ToList();
        if (past.Count == 0) return [];

        var ahead = Enumerable.Range(0, horizonDays).Select(today.AddDays).ToList();

        var result = new Dictionary<Guid, decimal>();
        foreach (var product in past.GroupBy(u => u.ProductId))
        {
            var byDay = product.ToDictionary(u => u.Day, u => u.Quantity);
            var first = product.Min(u => u.Day);
            var days = Enumerable.Range(0, today.DayNumber - first.DayNumber).Select(first.AddDays).ToList();
            // День без продаж — тоже наблюдение: расход в этот день был нулевым.
            decimal Average(IReadOnlyCollection<DateOnly> sample) => sample.Sum(d => byDay.GetValueOrDefault(d)) / sample.Count;

            var overall = Average(days);
            var expected = ahead.Sum(day =>
            {
                var same = days.Where(d => d.DayOfWeek == day.DayOfWeek).ToList();
                return same.Count > 0 ? Average(same) : overall;
            });
            result[product.Key] = expected / (horizonDays * 24);
        }
        return result;
    }
}
