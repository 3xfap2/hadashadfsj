using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record OrderLine(Guid DishId, int Quantity);

public sealed class OrderService(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Проверяет ингредиенты и списывает их одной транзакцией. Годные партии нужных продуктов
    /// блокируются на время заказа, поэтому два одновременных заказа не спишут один и тот же остаток.
    /// Заказ с нехваткой сохраняется как отклонённый — по ним считается статистика дефицита.
    /// </summary>
    public async Task<Order> PlaceAsync(IReadOnlyCollection<OrderLine> lines, string place, CancellationToken ct)
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
            Items = quantities.Select(q => new OrderItem { DishId = q.Key, Quantity = q.Value }).ToList(),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var productIds = required.Keys.ToArray();
        // Единый порядок блокировки во всех заказах исключает взаимные блокировки.
        var batches = await db.Batches
            .FromSql($"""
                SELECT * FROM "Batches"
                WHERE "ProductId" = ANY({productIds}) AND "Remaining" > 0 AND "ExpiresAt" > {now}
                ORDER BY "ExpiresAt", "Id"
                FOR UPDATE
                """)
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
