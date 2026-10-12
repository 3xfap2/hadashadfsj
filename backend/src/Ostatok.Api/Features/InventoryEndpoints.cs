using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Quantity">Сколько вскрыли; если не указано — всю партию.</param>
public sealed record OpenBatchRequest(decimal? Quantity);

/// <param name="Actual">Сколько продукта на складе по факту, вместе с просроченным, которое ещё не списали.</param>
public sealed record InventoryLine(Guid ProductId, decimal Actual);
public sealed record InventoryRequest(List<InventoryLine>? Items);

/// <param name="Value">Расхождение в рублях по закупочной цене: недостача — с минусом.</param>
public sealed record InventoryRow(Guid ProductId, string ProductName, Unit Unit, decimal Accounted, decimal Actual, decimal Difference, decimal Value);
/// <param name="Rows">В ответе на проведение — только продукты с расхождением; в документе — все пересчитанные.</param>
public sealed record InventoryResponse(Guid Id, int Number, decimal Shortage, decimal Surplus, IEnumerable<InventoryRow> Rows);
/// <param name="Counted">Сколько продуктов пересчитали.</param>
/// <param name="Differences">У скольких из них остаток не сошёлся с учётом.</param>
public sealed record InventorySummary(Guid Id, int Number, DateTimeOffset At, string Actor, int Counted, int Differences, decimal Shortage, decimal Surplus);

public static class InventoryEndpoints
{
    public const string Note = "Инвентаризация";

    public static void MapInventory(this RouteGroupBuilder api)
    {
        var stock = api.MapGroup("/stock").WithTags("Склад").RequireRoles(Access.Stock);

        // Вскрытая упаковка хранится меньше закрытой. Вскрытая часть становится отдельной партией с коротким сроком:
        // её первой спишет заказ, по ней же сработают сигналы и горячие предложения. Остаток партии лежит как лежал.
        stock.MapPost("/batches/{id:guid}/open", async (Guid id, OpenBatchRequest? req, AppDbContext db, TimeProvider time, StockOptions options, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var batch = (await db.Batches
                .FromSql($"""SELECT * FROM "Batches" WHERE "Id" = {id} AND "RestaurantId" = {db.TenantId} FOR UPDATE""")
                .IgnoreQueryFilters()
                .ToListAsync(ct)).FirstOrDefault();
            if (batch is null) return Results.NotFound();

            var life = await db.Products.Where(p => p.Id == batch.ProductId).Select(p => p.OpenedShelfLifeHours).FirstAsync(ct);
            var quantity = Math.Round(req?.Quantity ?? batch.Remaining, 3);
            if (life is null) return new Errors().Add("product", "У продукта не задан срок после вскрытия. Укажите его в справочнике.").ToResult();
            if (batch.OpenedAt is not null) return Conflict("Эта партия уже вскрыта.");
            if (batch.Remaining == 0 || batch.ExpiresAt <= now) return Conflict("Партия пуста или просрочена: вскрывать нечего.");
            if (quantity < Validation.MinQuantity || quantity > batch.Remaining)
                return new Errors().Add("quantity", "Количество — от 0,001 до остатка партии.").ToResult();

            var expiresAt = now.AddHours(life.Value) < batch.ExpiresAt ? now.AddHours(life.Value) : batch.ExpiresAt;
            var opened = batch;
            if (quantity < batch.Remaining)
            {
                opened = new Batch { Id = Guid.NewGuid(), RestaurantId = batch.RestaurantId, SupplyId = batch.SupplyId, ProductId = batch.ProductId, Quantity = quantity, Remaining = quantity, UnitCost = batch.UnitCost };
                db.Batches.Add(opened);
                batch.Quantity -= quantity;
                batch.Remaining -= quantity;
            }
            (opened.ExpiresAt, opened.OpenedAt) = (expiresAt, now);
            db.Movements.Add(new Movement
            {
                RestaurantId = batch.RestaurantId, At = now, Type = MovementType.Opened, ProductId = batch.ProductId, BatchId = opened.Id,
                Quantity = quantity, Note = $"Упаковка вскрыта, годен до {expiresAt.ToOffset(TimeSpan.FromHours(options.UtcOffsetHours)):dd.MM HH:mm}", Actor = user.Identity?.Name ?? "",
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { opened.Id, opened.ExpiresAt });
        });

        // Инвентаризация: сотрудник вводит, сколько продукта есть на самом деле. Недостача уходит из партий
        // с самым ранним сроком, излишек добавляется к партии с самым поздним; обе операции видны в журнале.
        // Пересчёт сохраняется документом с номером: по нему видно, кто, когда и что насчитал.
        stock.MapPost("/inventory", async (InventoryRequest req, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var items = req.Items ?? [];
            var ids = items.Select(i => i.ProductId).ToArray();
            var errors = new Errors()
                .Check(items.Count is > 0 and <= 2000, "items", "Укажите фактический остаток хотя бы одного продукта.")
                .Check(ids.Distinct().Count() == ids.Length, "items", "Продукт указан дважды.")
                .Check(items.All(i => i.Actual is >= 0 and <= 1_000_000), "items", "Фактический остаток — от 0 до 1 000 000.");
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            if (errors.Check(products.Count == ids.Distinct().Count(), "items", "В списке есть неизвестный продукт.").Any) return errors.ToResult();

            var now = time.GetUtcNow();
            var actor = user.Identity?.Name ?? "";
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Номер берётся до блокировки партий — в том же порядке, что и при заказе, иначе они могли бы заблокировать друг друга.
            var number = await db.NextInventoryNumberAsync(ct);
            // Та же блокировка, что и при заказе: пересчёт не разойдётся с параллельной продажей.
            var batches = (await db.Batches
                .FromSql($"""
                    SELECT * FROM "Batches"
                    WHERE "ProductId" = ANY({ids}) AND "RestaurantId" = {db.TenantId}
                    ORDER BY "ExpiresAt", "Id"
                    FOR UPDATE
                    """)
                .IgnoreQueryFilters()
                .ToListAsync(ct)).ToLookup(b => b.ProductId);

            var rows = new List<InventoryRow>();
            foreach (var item in items)
            {
                var product = products[item.ProductId];
                var own = batches[product.Id].ToList();
                var accounted = own.Sum(b => b.Remaining);
                var actual = Math.Round(item.Actual, 3);
                var difference = actual - accounted;
                // Расхождение оценивается по цене тех партий, из которых ушло или в которые добавилось.
                var value = 0m;
                if (difference < 0)
                {
                    var left = -difference;
                    foreach (var batch in own.Where(b => b.Remaining > 0))
                    {
                        if (left == 0) break;
                        var take = Math.Min(left, batch.Remaining);
                        batch.Remaining -= take;
                        left -= take;
                        value -= take * batch.UnitCost;
                        db.Movements.Add(Move(MovementType.Shortage, batch, take));
                    }
                }
                else if (difference > 0)
                {
                    // Лишнее записывается в самую свежую годную партию: срок у неё известен, выдумывать новый не нужно.
                    var batch = own.LastOrDefault(b => b.ExpiresAt > now);
                    if (batch is null)
                        return errors.Add("items", $"{product.Name}: годных партий нет, излишек записать некуда. Оформите его поставкой.").ToResult();
                    batch.Remaining += difference;
                    if (batch.Remaining > batch.Quantity) batch.Quantity = batch.Remaining;
                    value = difference * batch.UnitCost;
                    db.Movements.Add(Move(MovementType.Surplus, batch, difference));
                }
                rows.Add(new InventoryRow(product.Id, product.Name, product.Unit, accounted, actual, difference, Math.Round(value, 2)));
            }
            var changed = rows.Where(r => r.Difference != 0).OrderBy(r => r.Value).ToList();
            var doc = new Inventory
            {
                Id = Guid.NewGuid(), Number = number, At = now, Actor = actor,
                Shortage = -changed.Where(r => r.Value < 0).Sum(r => r.Value), Surplus = changed.Where(r => r.Value > 0).Sum(r => r.Value),
                Items = rows.Select(r => new InventoryItem { ProductId = r.ProductId, Accounted = r.Accounted, Actual = r.Actual, Difference = r.Difference, Value = r.Value }).ToList(),
            };
            db.Inventories.Add(doc);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(new InventoryResponse(doc.Id, doc.Number, doc.Shortage, doc.Surplus, changed));

            Movement Move(MovementType type, Batch batch, decimal quantity) => new()
            {
                RestaurantId = batch.RestaurantId, At = now, Type = type, ProductId = batch.ProductId, BatchId = batch.Id,
                Quantity = quantity, Note = Note, Actor = actor,
            };
        }).Produces<InventoryResponse>();

        stock.MapGet("/inventories", async (AppDbContext db, CancellationToken ct, int take = 30) =>
            await db.Inventories
                .OrderByDescending(i => i.Number)
                .Take(Math.Clamp(take, 1, 200))
                .Select(i => new InventorySummary(i.Id, i.Number, i.At, i.Actor, i.Items.Count, i.Items.Count(x => x.Difference != 0), i.Shortage, i.Surplus))
                .ToListAsync(ct));

        stock.MapGet("/inventories/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Inventories.AsNoTracking().Include(i => i.Items).ThenInclude(x => x.Product).FirstOrDefaultAsync(i => i.Id == id, ct) is { } doc
                ? Results.Ok(new InventoryResponse(doc.Id, doc.Number, doc.Shortage, doc.Surplus, doc.Items.OrderBy(x => x.Value).ThenBy(x => x.Product!.Name)
                    .Select(x => new InventoryRow(x.ProductId, x.Product!.Name, x.Product.Unit, x.Accounted, x.Actual, x.Difference, x.Value))))
                : Results.NotFound())
        .Produces<InventoryResponse>();
    }

    private static IResult Conflict(string text) => Results.Problem(text, statusCode: StatusCodes.Status409Conflict);
}
