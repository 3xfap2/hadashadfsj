using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="ShelfLifeHours">Срок хранения этой партии; если не указан, берётся норматив продукта.</param>
public sealed record SupplyItemRequest(Guid ProductId, decimal Quantity, int? ShelfLifeHours);

/// <param name="Id">Идентификатор поставки во внешней системе; повторная отправка с тем же Id не создаст дубль.</param>
/// <param name="CreatedAt">Дата создания поставки; по умолчанию — текущее время.</param>
public sealed record SupplyRequest(Guid? Id, string? Number, DateTimeOffset? CreatedAt, List<SupplyItemRequest>? Items);

public sealed record SupplyBatchResponse(Guid BatchId, Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Remaining, DateTimeOffset ExpiresAt);
public sealed record SupplyResponse(Guid Id, string Number, DateTimeOffset CreatedAt, IEnumerable<SupplyBatchResponse> Items);

public static class SupplyEndpoints
{
    public static void MapSupplies(this RouteGroupBuilder api)
    {
        var supplies = api.MapGroup("/supplies").WithTags("Поставки");

        supplies.MapGet("", async (AppDbContext db, CancellationToken ct, int take = 50) =>
            (await db.Supplies
                .Include(s => s.Batches).ThenInclude(b => b.Product)
                .OrderByDescending(s => s.CreatedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(ct))
            .Select(ToResponse));

        supplies.MapPost("", async (SupplyRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var createdAt = req.CreatedAt?.ToUniversalTime() ?? now;
            var items = req.Items ?? [];

            var errors = new Errors()
                .Check(!string.IsNullOrWhiteSpace(req.Number) && req.Number.Length <= 40, "number", "Укажите номер поставки до 40 символов.")
                .Check(createdAt <= now, "createdAt", "Дата поставки не может быть в будущем.")
                .Check(items.Count > 0, "items", "В поставке должен быть хотя бы один продукт.")
                .Check(items.All(i => i.Quantity > 0), "items", "Количество каждого продукта должно быть больше нуля.")
                .Check(items.All(i => i.ShelfLifeHours is null or > 0), "items", "Срок хранения должен быть больше нуля.");

            var ids = items.Select(i => i.ProductId).Distinct().ToArray();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            errors.Check(products.Count == ids.Length, "items", "В поставке есть неизвестный продукт.");
            if (errors.Any) return errors.ToResult();

            var supply = new Supply { Id = req.Id ?? Guid.NewGuid(), Number = req.Number!.Trim(), CreatedAt = createdAt };
            foreach (var item in items)
            {
                var product = products[item.ProductId];
                var expiresAt = createdAt.AddHours(item.ShelfLifeHours ?? product.ShelfLifeHours);
                if (expiresAt <= now)
                    return errors.Add("items", $"{product.Name}: срок годности уже истёк, такую партию принять нельзя.").ToResult();

                supply.Batches.Add(new Batch
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Product = product,
                    Quantity = item.Quantity,
                    Remaining = item.Quantity,
                    ExpiresAt = expiresAt,
                });
            }

            if (await db.Supplies.AnyAsync(s => s.Id == supply.Id || s.Number == supply.Number, ct))
                return Results.Problem("Поставка с таким идентификатором или номером уже принята.", statusCode: StatusCodes.Status409Conflict);

            db.Supplies.Add(supply);
            db.Movements.AddRange(supply.Batches.Select(b => new Movement
            {
                At = now,
                Type = MovementType.Supply,
                ProductId = b.ProductId,
                BatchId = b.Id,
                Quantity = b.Quantity,
                Note = $"Поставка {supply.Number}",
            }));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/supplies/{supply.Id}", ToResponse(supply));
        });
    }

    private static SupplyResponse ToResponse(Supply s) => new(
        s.Id, s.Number, s.CreatedAt,
        s.Batches.Select(b => new SupplyBatchResponse(b.Id, b.ProductId, b.Product!.Name, b.Product.Unit, b.Quantity, b.Remaining, b.ExpiresAt)));
}
