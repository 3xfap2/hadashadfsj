using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Discount">Продать со скидкой горячего предложения, если она сейчас действует.</param>
public sealed record OrderLine(Guid DishId, int Quantity, bool Discount = false);

public sealed class OrderService(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Проверяет ингредиенты и списывает их одной транзакцией. Годные партии нужных продуктов
    /// блокируются на время заказа, поэтому два одновременных заказа не спишут один и тот же остаток.
    /// Заказ с нехваткой сохраняется как отклонённый — по ним считается статистика дефицита.
    /// </summary>
    public async Task<Order> PlaceAsync(
        IReadOnlyCollection<OrderLine> lines, string place, string actor, CancellationToken ct,
        IReadOnlyDictionary<Guid, (int Percent, int Portions)>? discounts = null)
    {
        var quantities = lines.GroupBy(l => l.DishId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var dishes = await db.Dishes
            .Include(d => d.Recipe)
            .Where(d => quantities.Keys.Contains(d.Id))
            .ToListAsync(ct);

        var required = new Dictionary<Guid, decimal>();
        foreach (var dish in dishes)
            foreach (var item in dish.Recipe)
                required[item.ProductId] = required.GetValueOrDefault(item.ProductId) + item.Quantity * quantities[dish.Id];

        var now = time.GetUtcNow();
        var order = new Order
        {
            Id = Guid.NewGuid(),
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

        // Счётчик заказов ресторана: строка блокируется до конца транзакции, поэтому номера идут подряд без повторов.
        order.Number = (await db.Database
            .SqlQuery<int>($"""UPDATE "Restaurants" SET "LastOrderNumber" = "LastOrderNumber" + 1 WHERE "Id" = {db.TenantId} RETURNING "LastOrderNumber" """)
            .ToListAsync(ct)).Single();

        var productIds = required.Keys.ToArray();
        // Единый порядок блокировки во всех заказах исключает взаимные блокировки.
        var batches = await db.Batches
            .FromSql($"""
                SELECT * FROM "Batches"
                WHERE "ProductId" = ANY({productIds}) AND "Remaining" > 0 AND "ExpiresAt" > {now}
                ORDER BY "ExpiresAt", "Id"
                FOR UPDATE
                """)
            .IgnoreQueryFilters()   // блюда уже отобраны по ресторану, а запрос с блокировкой нельзя оборачивать
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
