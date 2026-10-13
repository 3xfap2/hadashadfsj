namespace Ostatok.Api.Domain;

public sealed record PurchaseSuggestion(
    Guid ProductId, string ProductName, Unit Unit,
    decimal Quantity, decimal Fresh, decimal PerDay, double? HoursCovered, string Reason);

public static class Forecast
{
    private const double TenYearsHours = 24 * 365 * 10;

    /// <summary>
    /// Момент, когда годного остатка продукта станет меньше порога.
    /// Учитывает и расход (из партии с ранним сроком), и то, что партии истекают сами.
    /// </summary>
    public static DateTimeOffset RunOutAt(IEnumerable<Batch> productBatches, decimal perHour, decimal threshold, DateTimeOffset now)
    {
        var batches = productBatches
            .Where(b => b.Remaining > 0 && b.ExpiresAt > now)
            .OrderBy(b => b.ExpiresAt)
            .ToList();
        var left = batches.Select(b => b.Remaining).ToArray();

        var t = now;
        for (var i = 0; i < batches.Count; i++)
        {
            var total = left.Skip(i).Sum();
            if (total < threshold) return t;

            var expiry = batches[i].ExpiresAt;
            if (perHour > 0)
            {
                // Запаса на годы вперёд хватает «дольше любого горизонта»: без границы дата вышла бы за пределы календаря.
                var hours = Math.Min((double)((total - threshold) / perHour), TenYearsHours);
                var hit = t.AddHours(hours);
                if (hit <= expiry) return hit;

                var consume = perHour * (decimal)(expiry - t).TotalHours;
                for (var j = i; j < left.Length && consume > 0; j++)
                {
                    var take = Math.Min(consume, left[j]);
                    left[j] -= take;
                    consume -= take;
                }
            }
            // Партия истекла: всё, что в ней осталось, пропадает.
            t = expiry;
        }
        return t;
    }

    /// <summary>Когда блюдо уйдёт в стоп, если ничего не привезут; null — не раньше, чем через горизонт.</summary>
    public static DateTimeOffset? DishStopAt(
        Dish dish, ILookup<Guid, Batch> batches, IReadOnlyDictionary<Guid, decimal> perHour,
        DateTimeOffset now, double horizonHours)
    {
        var at = dish.Recipe.Min(r => RunOutAt(batches[r.ProductId], perHour.GetValueOrDefault(r.ProductId), r.Quantity, now));
        return at <= now.AddHours(horizonHours) ? at : null;
    }

    /// <summary>
    /// Что и сколько заказать, чтобы запаса хватило на coverHours (но не дольше срока хранения продукта).
    /// Продукт попадает в список, если годного остатка не хватает на порцию или он закончится раньше срока покрытия.
    /// </summary>
    public static IReadOnlyList<PurchaseSuggestion> Purchases(
        IReadOnlyCollection<Product> products, IReadOnlyCollection<Dish> dishes,
        ILookup<Guid, Batch> batches, IReadOnlyDictionary<Guid, decimal> perHour,
        DateTimeOffset now, double coverHours)
    {
        const int PortionsWhenNoHistory = 10;
        var result = new List<PurchaseSuggestion>();

        foreach (var product in products)
        {
            var usedIn = dishes.Where(d => d.Recipe.Any(r => r.ProductId == product.Id)).ToList();
            if (usedIn.Count == 0) continue;

            var portion = usedIn.Min(d => d.Recipe.First(r => r.ProductId == product.Id).Quantity);
            var largestPortion = usedIn.Max(d => d.Recipe.First(r => r.ProductId == product.Id).Quantity);
            var rate = perHour.GetValueOrDefault(product.Id);
            var fresh = batches[product.Id].Where(b => b.ExpiresAt > now).Sum(b => b.Remaining);
            // Скоропортящийся продукт нет смысла запасать дольше, чем он хранится.
            var cover = Math.Min(coverHours, product.ShelfLifeHours);

            decimal quantity;
            double? hoursCovered;
            string reason;
            if (fresh < portion)
            {
                quantity = Math.Max(rate * (decimal)cover, largestPortion * PortionsWhenNoHistory);
                hoursCovered = 0;
                reason = $"Годного остатка нет. В стоп-листе: {string.Join(", ", usedIn.Select(d => d.Name))}.";
            }
            else
            {
                // Без истории продаж спрос неизвестен: заказывать нечего, пока продукт есть.
                if (rate == 0) continue;
                var hours = (RunOutAt(batches[product.Id], rate, portion, now) - now).TotalHours;
                if (hours >= cover) continue;

                quantity = rate * (decimal)(cover - hours);
                // Ради пары порций заявку не собирают.
                if (quantity < largestPortion * 3) continue;
                hoursCovered = Math.Round(hours, 1);
                var byConsumption = (double)((fresh - portion) / rate);
                reason = hours < byConsumption - 0.5
                    ? $"Часть остатка истечёт раньше, чем израсходуется: годного запаса хватит на {Ru.Duration(hours)}."
                    : $"При расходе {Ru.Qty(Math.Round(rate * 24, 2), product.Unit)} в день остатка хватит на {Ru.Duration(hours)}.";
            }

            result.Add(new PurchaseSuggestion(
                product.Id, product.Name, product.Unit,
                RoundUp(quantity, product.Unit), fresh, Math.Round(rate * 24, 2), hoursCovered, reason));
        }

        return result.OrderBy(s => s.HoursCovered).ThenBy(s => s.ProductName).ToList();
    }

    private static decimal RoundUp(decimal quantity, Unit unit) =>
        unit == Unit.Pcs ? Math.Ceiling(quantity) : Math.Ceiling(quantity * 10) / 10;
}
