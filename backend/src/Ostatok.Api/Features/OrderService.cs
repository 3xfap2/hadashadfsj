using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Discount">Продать со скидкой горячего предложения, если она сейчас действует.</param>
public sealed record OrderLine(Guid DishId, int Quantity, bool Discount = false);

/// <summary>Блюдо сняли с продажи вручную, пока заказ оформлялся: заказ не создан, со склада ничего не списано.</summary>
public sealed class DishStoppedException(IReadOnlyList<string> names) : Exception($"Снято с продажи: {string.Join(", ", names)}.")
{
    public IReadOnlyList<string> Names { get; } = names;
}

public sealed class OrderService(AppDbContext db, TimeProvider time)
{
    /// <summary>Сколько заказ ждёт блокировку счётчика и партий, прежде чем ответить «повторите».</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Проверяет ингредиенты и списывает их одной транзакцией. Годные партии нужных продуктов
    /// блокируются на время заказа, поэтому два одновременных заказа не спишут один и тот же остаток.
    /// Заказ с нехваткой сохраняется как отклонённый — по ним считается статистика дефицита.
    /// Если клиент прислал идентификатор заказа и такой заказ уже есть, возвращается он: повтор запроса
    /// (двойное нажатие, обрыв связи) ничего не списывает второй раз.
    /// </summary>
    public async Task<Order> PlaceAsync(
        IReadOnlyCollection<OrderLine> lines, string place, string actor, CancellationToken ct,
        IReadOnlyDictionary<Guid, (int Percent, int Portions)>? discounts = null, Guid? id = null)
    {
        var quantities = lines.GroupBy(l => l.DishId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var dishes = await db.Dishes
            .Include(d => d.Recipe)
            .Where(d => quantities.Keys.Contains(d.Id))
            .ToListAsync(ct);
        // Блюдо удалили между проверкой запроса и заказом: клиент получит 404 «запись удалена» (DbErrors), а не сбой сервера.
        if (dishes.Count < quantities.Count) throw new DbUpdateConcurrencyException("Блюдо из заказа удалено.");

        var required = new Dictionary<Guid, decimal>();
        foreach (var dish in dishes)
            foreach (var item in dish.Recipe)
                required[item.ProductId] = required.GetValueOrDefault(item.ProductId) + item.Quantity * quantities[dish.Id];

        var now = time.GetUtcNow();
        var order = new Order
        {
            Id = id ?? Guid.NewGuid(),
            CreatedAt = now,
            Place = place,
            Items = quantities.Select(q =>
            {
                var discount = discounts?.GetValueOrDefault(q.Key) ?? default;
                return new OrderItem
                {
                    DishId = q.Key, Quantity = q.Value, Price = dishes.First(d => d.Id == q.Key).Price,
                    DiscountPercent = discount.Portions > 0 ? discount.Percent : 0,
                    DiscountQuantity = Math.Min(discount.Portions, q.Value),
                };
            }).ToList(),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Если чужая транзакция зависла с блокировкой, заказ не ждёт бесконечно и не копит соединения: через 5 с — отказ «повторите».
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{LockTimeout.TotalMilliseconds}ms'", ct);

        // Счётчик заказов ресторана: строка блокируется до конца транзакции, поэтому номера идут подряд без повторов.
        order.Number = (await db.Database
            .SqlQuery<int>($"""UPDATE "Restaurants" SET "LastOrderNumber" = "LastOrderNumber" + 1 WHERE "Id" = {db.TenantId} RETURNING "LastOrderNumber" """)
            .ToListAsync(ct)).Single();

        // Проверка повтора идёт после блокировки счётчика: два одновременных запроса с одним идентификатором
        // выстраиваются в очередь, и второй видит заказ первого. Транзакция откатывается, номер не тратится.
        if (id is not null && await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) is { } repeated)
            return repeated;

        // Повар мог снять блюдо с продажи уже после проверки запроса: перечитываем флаг в транзакции.
        // FOR SHARE дождётся его незавершённого изменения, и заказ увидит уже снятое блюдо. Транзакция откатывается, номер не тратится.
        var dishIds = quantities.Keys.ToArray();
        var paused = await db.Database
            .SqlQuery<string>($"""SELECT "Name" AS "Value" FROM "Dishes" WHERE "Id" = ANY({dishIds}) AND "RestaurantId" = {db.TenantId} AND "ManuallyStopped" FOR SHARE""")
            .ToListAsync(ct);
        if (paused.Count > 0) throw new DishStoppedException(paused);

        var productIds = required.Keys.ToArray();
        // Партии, оставшиеся в контексте от прошлого заказа (генератор оформляет несколько заказов в одном контексте), устарели:
        // EF вернул бы их вместо свежих значений из запроса с блокировкой и затёр бы списание, сделанное между заказами.
        foreach (var stale in db.ChangeTracker.Entries<Batch>().ToList()) stale.State = EntityState.Detached;
        // Единый порядок блокировки во всех заказах исключает взаимные блокировки.
        var batches = await db.Batches
            .FromSql($"""
                SELECT * FROM "Batches"
                WHERE "ProductId" = ANY({productIds}) AND "RestaurantId" = {db.TenantId} AND "Remaining" > 0 AND "ExpiresAt" > {now}
                ORDER BY "ExpiresAt", "Id"
                FOR UPDATE
                """)
            .IgnoreQueryFilters()   // запрос с блокировкой нельзя оборачивать, поэтому ресторан указан в нём самом
            .ToListAsync(ct);

        var result = Fefo.Allocate(batches, required, now);
        if (result.Ok)
        {
            order.Status = OrderStatus.Accepted;
            var byId = batches.ToDictionary(b => b.Id);
            foreach (var a in result.Allocations)
            {
                byId[a.BatchId].Remaining -= a.Quantity;
                db.Movements.Add(new Movement
                {
                    At = now,
                    Type = MovementType.Order,
                    ProductId = a.ProductId,
                    BatchId = a.BatchId,
                    Quantity = a.Quantity,
                    OrderId = order.Id,
                    Actor = actor,
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

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return order;
    }
}
