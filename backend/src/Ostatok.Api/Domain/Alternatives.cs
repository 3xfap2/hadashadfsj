namespace Ostatok.Api.Domain;

/// <param name="Portions">Сколько порций можно приготовить сверх остального заказа; пусто — у блюда нет рецепта.</param>
/// <param name="Hot">Блюдо из горячих предложений: его стоит продать, пока продукт не истёк.</param>
public sealed record DishAlternative(Guid DishId, string DishName, decimal Price, int? Portions, bool Hot, string Reason);

/// <param name="DishId">Блюдо из заказа, которое не собрать.</param>
/// <param name="Quantity">Сколько порций его заказали — столько же порций замены.</param>
public sealed record DishReplacement(Guid DishId, string DishName, int Quantity, IReadOnlyList<DishAlternative> Options);

/// <summary>
/// Чем заменить блюда отклонённого заказа. Для каждого блюда, которое не собрать (снято с продажи или в рецепте
/// продукт, которого не хватило), — до трёх блюд в продаже, которых хватит на то же количество порций вместе
/// с остальным заказом. Цена — в пределах ±50 % от исходной; само блюдо и блюда, уже лежащие в заказе, не предлагаются.
/// Порядок: тот же раздел меню, затем горячие предложения (в порядке их плана), затем ближе по цене.
/// </summary>
public static class Alternatives
{
    public const int Max = 3;
    public const decimal PriceSpread = 0.5m;

    public static IReadOnlyList<DishReplacement> For(
        IReadOnlyCollection<Dish> dishes,
        StockTotals stock,
        IReadOnlyList<HotOffer> hot,
        IReadOnlyDictionary<Guid, int> order,
        IReadOnlySet<Guid> missingProducts)
    {
        var byId = dishes.ToDictionary(d => d.Id);
        var failed = order.Keys.Where(byId.ContainsKey).Select(id => byId[id])
            .Where(d => d.ManuallyStopped || d.Recipe.Any(r => missingProducts.Contains(r.ProductId)))
            .ToList();
        if (failed.Count == 0) return [];

        // Остаток после блюд заказа, которые собрать можно: замена не должна отнять у них продукты.
        var left = new Dictionary<Guid, decimal>(stock.Fresh);
        foreach (var (id, quantity) in order)
        {
            if (!byId.TryGetValue(id, out var dish) || failed.Contains(dish)) continue;
            foreach (var r in dish.Recipe)
                left[r.ProductId] = Math.Max(0, left.GetValueOrDefault(r.ProductId) - r.Quantity * quantity);
        }
        var rest = new StockTotals(left, stock.WithExpired);
        var hotRank = hot.Select((o, i) => (o, i)).ToDictionary(x => x.o.DishId, x => x.i);

        var result = new List<DishReplacement>();
        foreach (var dish in failed)
        {
            var quantity = order[dish.Id];
            var options = dishes
                .Where(d => !order.ContainsKey(d.Id))
                .Where(d => dish.Price <= 0 || Math.Abs(d.Price - dish.Price) <= dish.Price * PriceSpread)
                .Select(d => (Dish: d, Availability.Of(d, rest).Portions))
                .Where(x => x.Portions >= quantity)
                .Select(x => (x.Dish, x.Portions,
                    Same: dish.Category is { Length: > 0 } && x.Dish.Category == dish.Category,
                    Rank: hotRank.TryGetValue(x.Dish.Id, out var rank) ? rank : int.MaxValue))
                .OrderByDescending(x => x.Same)
                .ThenBy(x => x.Rank)
                .ThenBy(x => Math.Abs(x.Dish.Price - dish.Price))
                .ThenBy(x => x.Dish.Name)
                .Take(Max)
                .Select(x => new DishAlternative(
                    x.Dish.Id, x.Dish.Name, x.Dish.Price, x.Portions == int.MaxValue ? null : x.Portions, x.Rank != int.MaxValue,
                    Explain(x.Same, x.Rank == int.MaxValue ? null : hot[x.Rank], x.Portions)))
                .ToList();
            if (options.Count > 0) result.Add(new DishReplacement(dish.Id, dish.Name, quantity, options));
        }
        return result;
    }

    private static string Explain(bool same, HotOffer? offer, int portions)
    {
        if (offer is not null)
        {
            var why = offer.Ingredients.FirstOrDefault() is { } main
                ? $"{main.ProductName.ToLowerInvariant()} истекает через {Ru.Duration(main.HoursLeft)}"
                : offer.Note ?? "добавлено управляющим";
            return (same ? "тот же раздел; " : "") + $"из горячих: {why}";
        }
        var onSale = portions == int.MaxValue ? "в продаже" : $"в продаже {portions} {Ru.Plural(portions, "порция", "порции", "порций")}";
        return same ? $"тот же раздел, {onSale}" : onSale;
    }
}
