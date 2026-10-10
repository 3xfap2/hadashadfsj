using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record PrepRequest(Guid ProductId, decimal Quantity);
public sealed record PrepShortage(Guid ProductId, string ProductName, Unit Unit, decimal Required, decimal Available);
public sealed record PrepResponse(Guid BatchId, string SupplyNumber, decimal Quantity, DateTimeOffset ExpiresAt, IEnumerable<PrepShortage> Shortages);

/// <param name="Quantity">Сколько продукта идёт на единицу заготовки.</param>
public sealed record PrepIngredient(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Fresh);

/// <param name="Suggested">Сколько приготовить, чтобы хватило на срок покрытия, но не дольше срока хранения; 0 — готовить не нужно.</param>
/// <param name="MaxQuantity">Сколько можно приготовить из годного сырья.</param>
public sealed record PrepRow(
    Guid ProductId, string ProductName, Unit Unit, int ShelfLifeHours, decimal Fresh, decimal PerDay,
    double? HoursCovered, decimal Suggested, string? Reason, decimal MaxQuantity, IEnumerable<PrepIngredient> Ingredients);

/// <summary>
/// Заготовка — продукт, который кухня делает сама: у него есть состав. Приготовление списывает сырьё
/// по тем же правилам, что и заказ, и создаёт партию заготовки со своим сроком годности.
/// </summary>
public sealed class PrepService(AppDbContext db, TimeProvider time)
{
    public const string Workshop = "Собственный цех";

    /// <summary>null — такой заготовки нет. При нехватке сырья ничего не списывается, возвращаются нехватки.</summary>
    public async Task<PrepResponse?> MakeAsync(Guid productId, decimal quantity, string actor, CancellationToken ct)
    {
        var prep = await db.Products.Include(p => p.PrepRecipe).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (prep is null || prep.PrepRecipe.Count == 0) return null;

        var required = prep.PrepRecipe.ToDictionary(i => i.ProductId, i => i.Quantity * quantity);
        var now = time.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var productIds = required.Keys.ToArray();
        // Тот же порядок блокировки, что и в заказе: заготовка и заказ не спишут один остаток дважды.
        var batches = await db.Batches
            .FromSql($"""
                SELECT * FROM "Batches"
                WHERE "ProductId" = ANY({productIds}) AND "Remaining" > 0 AND "ExpiresAt" > {now}
                ORDER BY "ExpiresAt", "Id"
                FOR UPDATE
                """)
            .ToListAsync(ct);

        var result = Fefo.Allocate(batches, required, now);
        if (!result.Ok)
        {
            var names = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            return new PrepResponse(Guid.Empty, "", 0, now,
                result.Shortages.Select(s => new PrepShortage(s.ProductId, names[s.ProductId].Name, names[s.ProductId].Unit, s.Required, s.Available)));
        }

        var byId = batches.ToDictionary(b => b.Id);
        foreach (var a in result.Allocations)
        {
            byId[a.BatchId].Remaining -= a.Quantity;
            db.Movements.Add(new Movement
            {
                At = now, Type = MovementType.PrepUse, ProductId = a.ProductId, BatchId = a.BatchId,
                Quantity = a.Quantity, Note = $"Заготовка: {prep.Name}", Actor = actor,
            });
        }

        // Заготовка приходит на склад как поставка из собственного цеха: у неё есть номер, партия и этикетка.
        var number = $"З-{await db.Supplies.CountAsync(s => s.Number.StartsWith("З-"), ct) + 1:0000}";
        var supply = new Supply { Id = Guid.NewGuid(), Number = number, Supplier = Workshop, CreatedAt = now };
        var batch = new Batch
        {
            Id = Guid.NewGuid(), SupplyId = supply.Id, ProductId = prep.Id,
            Quantity = quantity, Remaining = quantity, ExpiresAt = now.AddHours(prep.ShelfLifeHours),
        };
        supply.Batches.Add(batch);
        db.Supplies.Add(supply);
        db.Movements.Add(new Movement
        {
            At = now, Type = MovementType.PrepMade, ProductId = prep.Id, BatchId = batch.Id,
            Quantity = quantity, Note = $"Заготовка {number}", Actor = actor,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PrepResponse(batch.Id, number, quantity, batch.ExpiresAt, []);
    }
}

public static class PrepEndpoints
{
    public static void MapPreps(this RouteGroupBuilder api)
    {
        var preps = api.MapGroup("/preps").WithTags("Заготовки").RequireRoles(Access.Orders);

        // План заготовок: что есть, на сколько хватит и сколько приготовить.
        preps.MapGet("", async (AppDbContext db, TimeProvider time, StockOptions options, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var products = await db.Products.AsNoTracking().Include(p => p.PrepRecipe).ToListAsync(ct);
            var byId = products.ToDictionary(p => p.Id);
            var made = products.Where(p => p.PrepRecipe.Count > 0).ToList();
            var totals = await db.LoadTotalsAsync(now, ct);
            var perHour = await db.LoadConsumptionPerHourAsync(now, options, ct);
            var plan = Forecast.Purchases(
                    made, await db.LoadDishesAsync(ct),
                    (await db.LoadFreshBatchesAsync(now, ct)).ToLookup(b => b.ProductId),
                    perHour, now, options.PurchaseCoverHours)
                .ToDictionary(s => s.ProductId);

            return made.OrderBy(p => p.Name).Select(p =>
            {
                var suggestion = plan.GetValueOrDefault(p.Id);
                decimal Fresh(Guid id) => totals.Fresh.GetValueOrDefault(id);
                return new PrepRow(
                    p.Id, p.Name, p.Unit, p.ShelfLifeHours, Fresh(p.Id),
                    Math.Round(perHour.GetValueOrDefault(p.Id) * 24, 2),
                    suggestion?.HoursCovered, suggestion?.Quantity ?? 0, suggestion?.Reason,
                    Math.Floor(p.PrepRecipe.Min(i => Fresh(i.ProductId) / i.Quantity) * 10) / 10,
                    p.PrepRecipe.Select(i => new PrepIngredient(i.ProductId, byId[i.ProductId].Name, byId[i.ProductId].Unit, i.Quantity, Fresh(i.ProductId))));
            });
        });

        preps.MapPost("", async (PrepRequest req, PrepService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var errors = new Errors().Check(req.Quantity is > 0 and <= 1000, "quantity", "Количество — больше нуля и не больше 1000.");
            if (errors.Any) return errors.ToResult();

            var made = await service.MakeAsync(req.ProductId, req.Quantity, user.Identity?.Name ?? "", ct);
            if (made is null) return errors.Add("productId", "Такой заготовки нет: у продукта не задан состав.").ToResult();
            // Сырья не хватило: ничего не списано, в ответе — чего и сколько не хватает.
            return made.Shortages.Any()
                ? Results.Json(made, statusCode: StatusCodes.Status409Conflict)
                : Results.Created($"/api/stock/batches?productId={req.ProductId}", made);
        });
    }
}
