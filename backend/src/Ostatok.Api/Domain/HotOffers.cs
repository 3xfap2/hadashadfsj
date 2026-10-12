namespace Ostatok.Api.Domain;

/// <param name="AtRiskQuantity">Сколько продукта истекает в пределах горизонта.</param>
/// <param name="NearestQuantity">Сколько в партии с самым ранним сроком; HoursLeft — её срок.</param>
/// <param name="UsableQuantity">Сколько из этого план отдаёт этому блюду: то, что не забрали блюда выше по списку.</param>
/// <param name="AlternativeDishes">Сколько блюд в продаже используют этот продукт, включая это.</param>
/// <param name="ValueAtRisk">Стоимость доли этого блюда по закупочной цене; пусто, если цены показывать нельзя.</param>
public sealed record HotIngredient(
    Guid ProductId, string ProductName, Unit Unit,
    decimal AtRiskQuantity, decimal NearestQuantity, double HoursLeft, decimal UsableQuantity, decimal? ValueAtRisk,
    int AlternativeDishes, decimal Score);

/// <summary>Правила расчёта, которые менеджер может менять.</summary>
/// <param name="HorizonHours">Учитываются партии, истекающие в эти часы.</param>
/// <param name="UrgencyWeight">
/// Что важнее — срок или сумма потерь: 0 — только сумма, 50 — поровну, 100 — срок важнее вдвойне.
/// В формуле это степень срочности: срочность ^ (вес ÷ 50).
/// </param>
/// <param name="ShareAcrossDishes">Делить вклад продукта между блюдами, которые его используют (÷ √N).</param>
/// <param name="MaxDiscountPercent">Потолок рекомендуемой скидки; 0 — скидки не предлагать.</param>
public sealed record HotRules(double HorizonHours = 48, int UrgencyWeight = 50, bool ShareAcrossDishes = true, int MaxDiscountPercent = 40);

/// <param name="Percent">Рекомендуемая скидка, кратна 5.</param>
/// <param name="Price">Цена блюда со скидкой.</param>
/// <param name="FloorPrice">Ниже этой цены продавать невыгодно: столько стоят продукты блюда, которые не истекают.</param>
public sealed record HotDiscount(int Percent, decimal Price, decimal? FloorPrice, string Reason);

/// <param name="Portions">Сколько порций стоит продать, чтобы израсходовать истекающие продукты.</param>
/// <param name="ValueAtRisk">Сколько рублей продуктов блюдо спасает от списания; пусто, если цены показывать нельзя.</param>
public sealed record HotOffer(
    Guid DishId, string DishName, string? PhotoUrl, decimal Price, decimal Score,
    int Portions, int AvailablePortions, decimal? ValueAtRisk,
    IReadOnlyList<HotIngredient> Ingredients, string Reason, HotDiscount? Discount, bool Pinned);

/// <summary>Истекающий продукт, который план не смог пристроить: блюд с ним в продаже нет или на них не хватает остальных продуктов.</summary>
public sealed record HotLeftover(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, double HoursLeft, decimal? Value);

/// <param name="AtRiskValue">Сколько рублей продуктов истекает в пределах горизонта.</param>
/// <param name="PlannedValue">Сколько из них уйдёт в блюда, если продать порции из плана.</param>
public sealed record HotPlan(IReadOnlyList<HotOffer> Offers, decimal? AtRiskValue, decimal? PlannedValue, IReadOnlyList<HotLeftover> Leftovers);

/// <summary>
/// Горячие предложения: какие блюда продавать активнее, чтобы продукты не ушли в списание.
///
/// Приоритет блюда — сумма вкладов его ингредиентов с истекающим сроком. Вклад ингредиента:
///   Σ по истекающим партиям (количество, которое блюдо успеет израсходовать × цена × срочность) ÷ √N
/// где срочность = 1 − (часов до конца срока ÷ горизонт): чем ближе срок, тем ближе к единице;
/// N — число блюд в продаже с этим продуктом: если блюдо единственное, его вклад не делится.
/// Количество ограничено и остатком партии, и числом порций, на которое хватает остальных ингредиентов.
///
/// Приоритет задаёт порядок, а порции — план. Истекающий продукт делится между блюдами по порядку списка:
/// первое блюдо берёт, сколько успеет израсходовать, следующему достаётся остаток. Поэтому порции и рубли
/// разных блюд не пересекаются и в сумме не превышают того, что лежит на складе. Блюдо, которому ничего
/// не досталось, не предлагается; продукт, который не пристроен ни в одно блюдо, остаётся в плане отдельной строкой.
/// Горизонт, вес срочности, деление на √N и потолок скидки задаёт менеджер (<see cref="HotRules"/>);
/// он же может закрепить блюдо первым или убрать его из списка.
/// Официанту и повару закупочные цены не показываются (costs = false): список и порядок те же, но без сумм в рублях.
///
/// Скидка предлагается, только если при обычном спросе партия не успеет продаться до конца срока:
///   скидка = 40% × доля партии, которая не продастся × срочность, с округлением вниз до 5%
/// и не больше той, при которой цена ещё покрывает продукты блюда, которые не истекают:
/// истекающий продукт пропадёт в любом случае, а остальные можно продать позже по полной цене.
/// </summary>
public static class HotOffers
{
    public static IReadOnlyList<HotOffer> Rank(
        IReadOnlyCollection<Dish> dishes,
        IReadOnlyCollection<Batch> batches,
        StockTotals stock,
        DateTimeOffset now,
        HotRules rules,
        IReadOnlyDictionary<Guid, decimal>? perHour = null,
        bool costs = true) => Plan(dishes, batches, stock, now, rules, perHour, costs).Offers;

    public static HotPlan Plan(
        IReadOnlyCollection<Dish> dishes,
        IReadOnlyCollection<Batch> batches,
        StockTotals stock,
        DateTimeOffset now,
        HotRules rules,
        IReadOnlyDictionary<Guid, decimal>? perHour = null,
        bool costs = true)
    {
        var horizonHours = rules.HorizonHours;
        var horizon = now.AddHours(horizonHours);
        var atRisk = batches
            .Where(b => b.Remaining > 0 && b.ExpiresAt > now && b.ExpiresAt <= horizon)
            .OrderBy(b => b.ExpiresAt).ThenBy(b => b.Id)
            .ToLookup(b => b.ProductId);

        var onSale = dishes
            .Select(d => (Dish: d, Availability.Of(d, stock).Portions))
            .Where(x => x.Portions > 0)
            .ToList();
        // Скрытое блюдо остаётся в продаже и по-прежнему расходует продукт — оно только не предлагается.

        var dishesByProduct = onSale
            .SelectMany(x => x.Dish.Recipe.Select(r => r.ProductId))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        // Шаг 1 — приоритет: сколько каждое блюдо могло бы спасти, если бы истекающий продукт достался только ему.
        var candidates = new List<(Dish Dish, int Portions, List<HotIngredient> Ingredients, decimal Score)>();
        foreach (var (dish, portions) in onSale.Where(x => !x.Dish.HotHidden))
        {
            var ingredients = new List<HotIngredient>();
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
                    weighted += take * product.Cost * (decimal)Math.Pow(urgency, rules.UrgencyWeight / 50.0);
                    left -= take;
                }

                var alternatives = dishesByProduct[item.ProductId];
                ingredients.Add(new HotIngredient(
                    product.Id, product.Name, product.Unit,
                    atRiskQuantity, expiring[0].Remaining, (expiring[0].ExpiresAt - now).TotalHours, usable, usable * product.Cost,
                    alternatives, rules.ShareAcrossDishes ? weighted / (decimal)Math.Sqrt(alternatives) : weighted));
            }

            if (ingredients.Count > 0) candidates.Add((dish, portions, ingredients, Math.Round(ingredients.Sum(i => i.Score), 2)));
        }

        // Шаг 2 — план: истекающий продукт раздаётся блюдам по порядку приоритета, каждому — из того, что осталось.
        var cost = atRisk.ToDictionary(g => g.Key, g => g.First().Product!.Cost);
        var free = atRisk.ToDictionary(g => g.Key, g => g.Sum(b => b.Remaining));
        var offers = new List<HotOffer>();
        foreach (var (dish, portions, potential, score) in candidates
            .OrderByDescending(c => c.Dish.HotPinned).ThenByDescending(c => c.Score).ThenBy(c => c.Dish.Name))
        {
            var perPortion = dish.Recipe.ToDictionary(r => r.ProductId, r => r.Quantity);
            // Порций нужно столько, чтобы израсходовать нераспределённый остаток самого «долгого» из истекающих продуктов блюда.
            var planned = Math.Min(portions, potential.Max(i => (int)Math.Ceiling(Math.Min(free[i.ProductId], perPortion[i.ProductId] * portions) / perPortion[i.ProductId])));
            if (planned == 0) continue;

            var ingredients = new List<HotIngredient>();
            foreach (var ingredient in potential.OrderByDescending(i => i.Score))
            {
                var share = Math.Min(free[ingredient.ProductId], perPortion[ingredient.ProductId] * planned);
                if (share == 0) continue;
                free[ingredient.ProductId] -= share;
                ingredients.Add(ingredient with { UsableQuantity = share, ValueAtRisk = share * cost[ingredient.ProductId] });
            }

            offers.Add(new HotOffer(
                dish.Id, dish.Name, dish.PhotoUrl, dish.Price, score,
                planned, portions,
                ingredients.Sum(i => i.ValueAtRisk),
                ingredients,
                Explain(ingredients, planned, horizonHours, costs),
                Discount(dish, ingredients, perHour, rules, costs),
                dish.HotPinned));
        }

        var leftovers = atRisk
            .Where(g => free[g.Key] > 0)
            .Select(g => new HotLeftover(g.Key, g.First().Product!.Name, g.First().Product!.Unit, free[g.Key],
                (g.First().ExpiresAt - now).TotalHours, costs ? Math.Round(free[g.Key] * cost[g.Key], 2) : null))
            .OrderBy(l => l.HoursLeft)
            .ToList();
        var atRiskValue = atRisk.Sum(g => g.Sum(b => b.Remaining) * cost[g.Key]);
        var plannedValue = offers.Sum(o => o.ValueAtRisk) ?? 0;
        if (costs) return new HotPlan(offers, Math.Round(atRiskValue, 2), Math.Round(plannedValue, 2), leftovers);
        // Приоритет — это рубли с весом срочности, поэтому вместе с суммами скрывается и он; порядок списка остаётся.
        return new HotPlan(offers.Select(o => o with
        {
            Score = 0, ValueAtRisk = null,
            Ingredients = o.Ingredients.Select(i => i with { Score = 0, ValueAtRisk = null }).ToList(),
            Discount = o.Discount is null ? null : o.Discount with { FloorPrice = null },
        }).ToList(), null, null, leftovers);
    }

    private static HotDiscount? Discount(
        Dish dish, IReadOnlyList<HotIngredient> ingredients, IReadOnlyDictionary<Guid, decimal>? perHour, HotRules rules, bool costs)
    {
        if (dish.Price <= 0) return null;
        var main = ingredients[0];
        var expiring = ingredients.Select(i => i.ProductId).ToHashSet();
        var floor = dish.Recipe.Where(r => !expiring.Contains(r.ProductId)).Sum(r => r.Quantity * (r.Product?.Cost ?? 0));
        var allowed = (double)(1 - floor / dish.Price) * 100;

        var expected = (perHour?.GetValueOrDefault(main.ProductId) ?? 0) * (decimal)main.HoursLeft;
        var unsold = (double)Math.Clamp(1 - expected / main.NearestQuantity, 0, 1);
        var urgency = 1 - main.HoursLeft / rules.HorizonHours;
        var percent = (int)Math.Floor(Math.Min(allowed, rules.MaxDiscountPercent * unsold * urgency) / 5) * 5;
        if (percent < 5) return null;

        var price = Math.Round(dish.Price * (100 - percent) / 100);
        var pace = expected > 0
            ? $"При обычном спросе до конца срока уйдёт около {Ru.Qty(Math.Round(expected, 2), main.Unit)} из {Ru.Qty(main.NearestQuantity, main.Unit)}"
            : $"Спроса на {main.ProductName.ToLowerInvariant()} в истории продаж пока нет";
        return new HotDiscount(percent, price, Math.Round(floor),
            $"{pace}. Скидка {percent}% — {Ru.Money(price)} вместо {Ru.Money(dish.Price)}."
            + (costs ? $" Ниже {Ru.Money(floor)} продавать невыгодно: столько стоят остальные продукты блюда." : ""));
    }

    private static string Explain(IReadOnlyList<HotIngredient> ingredients, int portions, double horizonHours, bool costs)
    {
        var main = ingredients[0];
        var text = $"{main.ProductName}: {Ru.Qty(main.NearestQuantity, main.Unit)} истекает через {Ru.Duration(main.HoursLeft)}";
        if (main.AtRiskQuantity > main.NearestQuantity)
            text += $" (всего {Ru.Qty(main.AtRiskQuantity, main.Unit)} за {Ru.Duration(horizonHours)})";
        text += costs
            ? $". Продайте {Ru.Portions(portions)} — это {Ru.Money(ingredients.Sum(i => i.ValueAtRisk) ?? 0)} продуктов, которые иначе уйдут в списание."
            : $". Продайте {Ru.Portions(portions)}, иначе продукты уйдут в списание.";
        if (main.AlternativeDishes == 1) text += " Других блюд с этим продуктом в продаже нет.";
        else if (main.UsableQuantity < main.AtRiskQuantity) text += $" На это блюдо приходится {Ru.Qty(main.UsableQuantity, main.Unit)}, остальное — в других блюдах плана.";
        if (ingredients.Count > 1) text += $" Также истекает: {string.Join(", ", ingredients.Skip(1).Select(i => i.ProductName.ToLowerInvariant()))}.";
        return text;
    }
}
