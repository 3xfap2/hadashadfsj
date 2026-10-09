namespace Ostatok.Api.Domain;

/// <param name="AtRiskQuantity">Сколько продукта истекает в пределах горизонта.</param>
/// <param name="NearestQuantity">Сколько в партии с самым ранним сроком; HoursLeft — её срок.</param>
/// <param name="UsableQuantity">Сколько из этого блюдо успеет израсходовать при доступном числе порций.</param>
/// <param name="AlternativeDishes">Сколько блюд в продаже используют этот продукт, включая это.</param>
public sealed record HotIngredient(
    Guid ProductId, string ProductName, Unit Unit,
    decimal AtRiskQuantity, decimal NearestQuantity, double HoursLeft, decimal UsableQuantity, decimal ValueAtRisk,
    int AlternativeDishes, decimal Score);

/// <param name="Portions">Сколько порций стоит продать, чтобы израсходовать истекающие продукты.</param>
/// <param name="ValueAtRisk">Сколько рублей продуктов блюдо спасает от списания.</param>
public sealed record HotOffer(
    Guid DishId, string DishName, string? PhotoUrl, decimal Price, decimal Score,
    int Portions, int AvailablePortions, decimal ValueAtRisk,
    IReadOnlyList<HotIngredient> Ingredients, string Reason);

/// <summary>
/// Горячие предложения: какие блюда продавать активнее, чтобы продукты не ушли в списание.
///
/// Приоритет блюда — сумма вкладов его ингредиентов с истекающим сроком. Вклад ингредиента:
///   Σ по истекающим партиям (количество, которое блюдо успеет израсходовать × цена × срочность) ÷ √N
/// где срочность = 1 − (часов до конца срока ÷ горизонт): чем ближе срок, тем ближе к единице;
/// N — число блюд в продаже с этим продуктом: если блюдо единственное, его вклад не делится.
/// Количество ограничено и остатком партии, и числом порций, на которое хватает остальных ингредиентов.
/// </summary>
public static class HotOffers
{
    public static IReadOnlyList<HotOffer> Rank(
        IReadOnlyCollection<Dish> dishes,
        IReadOnlyCollection<Batch> batches,
        StockTotals stock,
        DateTimeOffset now,
        double horizonHours)
    {
        var horizon = now.AddHours(horizonHours);
        var atRisk = batches
            .Where(b => b.Remaining > 0 && b.ExpiresAt > now && b.ExpiresAt <= horizon)
            .OrderBy(b => b.ExpiresAt).ThenBy(b => b.Id)
            .ToLookup(b => b.ProductId);

        var onSale = dishes
            .Select(d => (Dish: d, Availability.Of(d, stock).Portions))
            .Where(x => x.Portions > 0)
            .ToList();

        var dishesByProduct = onSale
            .SelectMany(x => x.Dish.Recipe.Select(r => r.ProductId))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var offers = new List<HotOffer>();
        foreach (var (dish, portions) in onSale)
        {
            var ingredients = new List<HotIngredient>();
            var recommended = 0;
            foreach (var item in dish.Recipe)
            {
                var expiring = atRisk[item.ProductId].ToList();
                if (expiring.Count == 0) continue;

                var product = expiring[0].Product!;
                var atRiskQuantity = expiring.Sum(b => b.Remaining);
                var usable = Math.Min(atRiskQuantity, item.Quantity * portions);

                var weighted = 0m;
                var left = usable;
                foreach (var batch in expiring)
                {
                    if (left == 0) break;
                    var take = Math.Min(left, batch.Remaining);
                    var urgency = 1 - (batch.ExpiresAt - now).TotalHours / horizonHours;
                    weighted += take * product.Cost * (decimal)urgency;
                    left -= take;
                }

                var alternatives = dishesByProduct[item.ProductId];
                ingredients.Add(new HotIngredient(
                    product.Id, product.Name, product.Unit,
                    atRiskQuantity, expiring[0].Remaining, (expiring[0].ExpiresAt - now).TotalHours, usable, usable * product.Cost,
                    alternatives, weighted / (decimal)Math.Sqrt(alternatives)));
                recommended = Math.Max(recommended, (int)Math.Ceiling(usable / item.Quantity));
            }

            if (ingredients.Count == 0) continue;
            ingredients = ingredients.OrderByDescending(i => i.Score).ToList();
            offers.Add(new HotOffer(
                dish.Id, dish.Name, dish.PhotoUrl, dish.Price,
                Math.Round(ingredients.Sum(i => i.Score), 2),
                Math.Min(recommended, portions), portions,
                ingredients.Sum(i => i.ValueAtRisk),
                ingredients,
                Explain(ingredients, Math.Min(recommended, portions), horizonHours)));
        }

        return offers.OrderByDescending(o => o.Score).ThenBy(o => o.DishName).ToList();
    }

    private static string Explain(IReadOnlyList<HotIngredient> ingredients, int portions, double horizonHours)
    {
        var main = ingredients[0];
        var text = $"{main.ProductName}: {Ru.Qty(main.NearestQuantity, main.Unit)} истекает через {Ru.Duration(main.HoursLeft)}";
        if (main.AtRiskQuantity > main.NearestQuantity)
            text += $" (всего {Ru.Qty(main.AtRiskQuantity, main.Unit)} за {Ru.Duration(horizonHours)})";
        text += $". Продайте {Ru.Portions(portions)} — это {Ru.Money(ingredients.Sum(i => i.ValueAtRisk))} продуктов, которые иначе уйдут в списание.";
        if (main.AlternativeDishes == 1) text += " Других блюд с этим продуктом в продаже нет.";
        if (ingredients.Count > 1) text += $" Также истекает: {string.Join(", ", ingredients.Skip(1).Select(i => i.ProductName.ToLowerInvariant()))}.";
        return text;
    }
}
