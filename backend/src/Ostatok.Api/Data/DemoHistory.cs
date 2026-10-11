using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

/// <summary>
/// Прошлое демо-ресторана: две недели поставок, заказов и списаний до сегодняшнего дня.
/// Это не набор случайных записей, а прогон тех же правил, что работают в системе: продукты списываются
/// из партий с ранним сроком, заказ с нехваткой отклоняется, просрочка списывается. Поэтому аналитика,
/// расход и прогнозы на этих данных сходятся между собой.
/// </summary>
public static class DemoHistory
{
    private const int Days = 14;
    private const int OrdersPerDay = 34;
    private const decimal DishesPerOrder = 1.8m;   // в среднем: одно-два блюда, иногда двойная порция
    private static readonly TimeSpan Zone = TimeSpan.FromHours(3);

    private static readonly Dictionary<string, int> Popularity = new()
    {
        ["Цезарь с курицей"] = 6, ["Том ям с креветками"] = 5, ["Пицца «Маргарита»"] = 5,
        ["Феттучине с лососем"] = 4, ["Цезарь с креветками"] = 4, ["Стейк из лосося"] = 3,
        ["Боул с лососем и авокадо"] = 2, ["Филе миньон"] = 2, ["Тартар из говядины"] = 2,
    };

    private static readonly Dictionary<string, string> Suppliers = new()
    {
        ["Лосось охлаждённый"] = "«Северная рыба»", ["Креветки тигровые"] = "«Северная рыба»",
        ["Говяжья вырезка"] = "Мясной двор «Ока»", ["Куриное филе"] = "Птицефабрика «Лесная»",
        ["Сливки 33%"] = "Молочный двор", ["Моцарелла"] = "Молочный двор", ["Пармезан"] = "Молочный двор",
        ["Яйцо куриное"] = "Ферма «Ясное»",
        ["Основа том ям"] = "Собственный цех", ["Тесто для пиццы"] = "Собственный цех", ["Соус цезарь"] = "Собственный цех",
    };

    private static readonly string[] Places = ["Стол 1", "Стол 2", "Стол 3", "Стол 4", "Стол 5", "Стол 7", "Стол 9", "Стол 12", "Бар"];

    public static void Add(AppDbContext db, IReadOnlyList<Product> products, IReadOnlyList<Dish> dishes, DateTimeOffset now)
    {
        var random = new Random(20261009);   // одно и то же прошлое при каждом пересоздании базы
        var productById = products.ToDictionary(p => p.Id);
        var weights = dishes.ToDictionary(d => d.Id, d => Popularity.GetValueOrDefault(d.Name, 1));
        var totalWeight = weights.Values.Sum();

        // Сколько каждого продукта уходит за обычный день.
        var dailyUse = products.ToDictionary(p => p.Id, _ => 0m);
        foreach (var dish in dishes)
            foreach (var item in dish.Recipe)
                dailyUse[item.ProductId] += item.Quantity * OrdersPerDay * DishesPerOrder * weights[dish.Id] / totalWeight;

        var batches = new List<Batch>();
        var supplies = new List<Supply>();
        var orders = new List<Order>();
        var movements = new List<Movement>();
        var logs = new List<StopLog>();
        var openLogs = new Dictionary<Guid, StopLog>();
        var supplyNumber = 180;
        // Полночь по времени ресторана, записанная в UTC: база хранит время только так.
        var today = new DateTimeOffset(now.ToOffset(Zone).Date, Zone).ToUniversalTime();

        StockTotals Totals(DateTimeOffset t) => new(
            batches.Where(b => b.Remaining > 0 && b.ExpiresAt > t).GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.Sum(b => b.Remaining)),
            batches.Where(b => b.Remaining > 0 && b.ExpiresAt <= t).Select(b => b.ProductId).ToHashSet());

        // Сверяет стоп-лист с остатками и ведёт интервалы простоя — как это делает сервер раз в минуту.
        void TrackStops(DateTimeOffset t)
        {
            var totals = Totals(t);
            foreach (var dish in dishes)
            {
                var state = Availability.Of(dish, totals);
                var stopped = state.Status == DishStatus.Stop;
                if (stopped && !openLogs.ContainsKey(dish.Id))
                    openLogs[dish.Id] = new StopLog { DishId = dish.Id, ProductId = state.LimitingProductId, Reason = state.Reason!.Value, StartedAt = t };
                else if (!stopped && openLogs.Remove(dish.Id, out var log))
                {
                    log.EndedAt = t;
                    logs.Add(log);
                }
            }
        }

        void WriteOffExpired(DateTimeOffset t)
        {
            foreach (var batch in batches.Where(b => b.Remaining > 0 && b.ExpiresAt.AddHours(12) <= t))
            {
                movements.Add(new Movement
                {
                    At = batch.ExpiresAt.AddHours(12), Type = MovementType.WriteOff, ProductId = batch.ProductId, BatchId = batch.Id,
                    Quantity = batch.Remaining, Note = "Истёк срок годности (автосписание)", Actor = "система",
                });
                batch.Remaining = 0;
            }
        }

        for (var day = Days; day >= 1; day--)
        {
            var morning = today.AddDays(-day).AddHours(9);
            // В пятницу и субботу гостей больше, в начале недели меньше; закупщик это знает и берёт соответственно.
            var weekday = morning.ToOffset(Zone).DayOfWeek switch
            {
                DayOfWeek.Friday or DayOfWeek.Saturday => 1.3,
                DayOfWeek.Monday or DayOfWeek.Tuesday => 0.75,
                _ => 1.0,
            };
            WriteOffExpired(morning);

            // Утренняя поставка: скоропорт каждый день, остальное раз в неделю. Объём «на глаз» — то больше, то меньше нужного,
            // поэтому в истории есть и списания, и дни, когда чего-то не хватило.
            var totals = Totals(morning);
            var wanted = new List<(Product Product, decimal Quantity)>();
            foreach (var product in products.Where(p => dailyUse[p.Id] > 0))
            {
                var perishable = product.ShelfLifeHours <= 120;
                if (!perishable && day % 7 != 0) continue;
                // Обычно берут с запасом; изредка по какому-то продукту заказывают слишком мало.
                var margin = random.Next(100) < 4 ? 0.55 + random.NextDouble() * 0.25 : 1.15 + random.NextDouble() * 0.45;
                var target = dailyUse[product.Id] * (perishable ? (decimal)(margin * weekday) : 8.5m);
                var quantity = target - totals.Fresh.GetValueOrDefault(product.Id);
                var rounded = product.Unit == Unit.Pcs ? Math.Ceiling(quantity) : Math.Round(quantity, 1);
                if (rounded > 0) wanted.Add((product, rounded));
            }
            foreach (var group in wanted.GroupBy(w => Suppliers.GetValueOrDefault(w.Product.Name, "Овощная база №3")))
            {
                var supply = new Supply { Id = Guid.NewGuid(), Number = $"П-{supplyNumber++:0000}", Supplier = group.Key, CreatedAt = morning };
                foreach (var (product, quantity) in group)
                {
                    var batch = new Batch
                    {
                        Id = Guid.NewGuid(), SupplyId = supply.Id, ProductId = product.Id, Product = product,
                        Quantity = quantity, Remaining = quantity, ExpiresAt = morning.AddHours(product.ShelfLifeHours),
                    };
                    supply.Batches.Add(batch);
                    batches.Add(batch);
                    movements.Add(new Movement
                    {
                        At = morning, Type = MovementType.Supply, ProductId = product.Id, BatchId = batch.Id,
                        Quantity = quantity, Note = $"Поставка {supply.Number}", Actor = "storekeeper",
                    });

                    // Изредка часть партии бракуют при приёмке; у овощной базы это случается заметно чаще.
                    if (random.Next(100) < (group.Key == "Овощная база №3" ? 16 : 3))
                    {
                        var share = (decimal)(0.1 + random.NextDouble() * 0.2);
                        var bad = product.Unit == Unit.Pcs ? Math.Floor(quantity * share) : Math.Round(quantity * share, 1);
                        if (bad > 0 && bad < quantity)
                        {
                            batch.Remaining -= bad;
                            movements.Add(new Movement
                            {
                                At = morning.AddMinutes(20), Type = MovementType.WriteOff, ProductId = product.Id, BatchId = batch.Id,
                                Quantity = bad, Note = "Брак поставки", Actor = "storekeeper",
                            });
                        }
                    }
                }
                supplies.Add(supply);
            }
            TrackStops(morning);

            // Заказы с полудня до одиннадцати вечера.
            // Гостей в разные дни разное число: в тихий день продукты остаются и потом списываются.
            var ordersToday = (int)(OrdersPerDay * weekday * (0.75 + random.NextDouble() * 0.4));
            for (var i = 0; i < ordersToday; i++)
            {
                var at = today.AddDays(-day).AddHours(12).AddMinutes(i * 660.0 / ordersToday + random.Next(0, 15));
                var lines = Enumerable.Range(0, random.Next(1, 3))
                    .Select(_ => PickDish(dishes, weights, totalWeight, random))
                    .GroupBy(d => d.Id)
                    .Select(g => (Dish: g.First(), Quantity: g.Count() + (random.Next(5) == 0 ? 1 : 0)))
                    .ToList();

                var required = new Dictionary<Guid, decimal>();
                foreach (var (dish, quantity) in lines)
                    foreach (var item in dish.Recipe)
                        required[item.ProductId] = required.GetValueOrDefault(item.ProductId) + item.Quantity * quantity;

                var order = new Order
                {
                    // Идентификатор растёт со временем: заказы вставляются по порядку, и номера идут подряд.
                    Id = Guid.CreateVersion7(at), CreatedAt = at, Place = Places[random.Next(Places.Length)],
                    Items = lines.Select(l => new OrderItem { DishId = l.Dish.Id, Quantity = l.Quantity, Price = l.Dish.Price }).ToList(),
                };
                var result = Fefo.Allocate(batches, required, at);
                if (result.Ok)
                {
                    order.Status = OrderStatus.Accepted;
                    var byId = batches.ToDictionary(b => b.Id);
                    foreach (var a in result.Allocations)
                    {
                        byId[a.BatchId].Remaining -= a.Quantity;
                        movements.Add(new Movement
                        {
                            At = at, Type = MovementType.Order, ProductId = a.ProductId, BatchId = a.BatchId,
                            Quantity = a.Quantity, OrderId = order.Id, Actor = "waiter",
                        });
                    }
                }
                else
                {
                    order.Status = OrderStatus.Rejected;
                    order.Shortages = result.Shortages
                        .Select(s => new OrderShortage { ProductId = s.ProductId, Required = s.Required, Available = s.Available })
                        .ToList();
                }
                orders.Add(order);
                TrackStops(at);
            }
        }

        // К сегодняшнему утру прошлое закрывается, чтобы текущие остатки были ровно такими, как задумано в демо:
        // просрочка списана, а то, что ещё годно, считается не привезённым — уменьшаем исходный объём партии.
        var supplyMovements = movements.Where(m => m.Type == MovementType.Supply).ToDictionary(m => m.BatchId);
        foreach (var batch in batches.Where(b => b.Remaining > 0))
        {
            if (batch.ExpiresAt <= now)
            {
                movements.Add(new Movement
                {
                    At = Earlier(batch.ExpiresAt.AddHours(12), now.AddMinutes(-30)), Type = MovementType.WriteOff, ProductId = batch.ProductId,
                    BatchId = batch.Id, Quantity = batch.Remaining, Note = "Истёк срок годности", Actor = "storekeeper",
                });
            }
            else
            {
                batch.Quantity -= batch.Remaining;
                supplyMovements[batch.Id].Quantity = batch.Quantity;
            }
            batch.Remaining = 0;
        }
        var empty = batches.Where(b => b.Quantity == 0).Select(b => b.Id).ToHashSet();
        foreach (var supply in supplies) supply.Batches.RemoveAll(b => empty.Contains(b.Id));
        movements.RemoveAll(m => empty.Contains(m.BatchId));

        var end = today.AddHours(-1);
        foreach (var log in openLogs.Values)
        {
            log.EndedAt = end;
            logs.Add(log);
        }

        db.Supplies.AddRange(supplies.Where(s => s.Batches.Count > 0));
        db.Orders.AddRange(orders);
        db.Movements.AddRange(movements.OrderBy(m => m.At));
        db.StopLogs.AddRange(logs.Where(l => l.EndedAt > l.StartedAt));
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static Dish PickDish(IReadOnlyList<Dish> dishes, Dictionary<Guid, int> weights, int total, Random random)
    {
        var roll = random.Next(total);
        foreach (var dish in dishes)
        {
            roll -= weights[dish.Id];
            if (roll < 0) return dish;
        }
        return dishes[^1];
    }
}
