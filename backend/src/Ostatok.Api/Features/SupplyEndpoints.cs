using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="ShelfLifeHours">Срок хранения этой партии; если не указан, берётся норматив продукта.</param>
public sealed record SupplyItemRequest(Guid ProductId, decimal Quantity, int? ShelfLifeHours);

/// <param name="Id">Идентификатор поставки во внешней системе; повторная отправка с тем же Id не создаст дубль.</param>
/// <param name="CreatedAt">Дата создания поставки; по умолчанию — текущее время.</param>
public sealed record SupplyRequest(Guid? Id, string? Number, DateTimeOffset? CreatedAt, List<SupplyItemRequest>? Items, string? Supplier = null);

public sealed record SupplyBatchResponse(Guid BatchId, Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Remaining, DateTimeOffset ExpiresAt);
/// <param name="Total">Стоимость поставки по закупочным ценам из справочника.</param>
public sealed record SupplyResponse(Guid Id, string Number, string? Supplier, DateTimeOffset CreatedAt, decimal Total, IEnumerable<SupplyBatchResponse> Items);

public sealed record IncomingItemResponse(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, int ShelfLifeHours);
/// <summary>Поставка от внешней системы, которая ждёт подтверждения.</summary>
public sealed record IncomingResponse(Guid Id, string Number, string? Supplier, DateTimeOffset ReceivedAt, decimal Total, IEnumerable<IncomingItemResponse> Items);

public static class SupplyEndpoints
{
    private const int MaxImportBytes = 1_000_000;

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

        // Предпросмотр файла поставки: тело запроса — сам файл CSV. Ничего не сохраняет;
        // после подтверждения строки без ошибок отправляются обычным POST /api/supplies.
        supplies.MapPost("/import/preview", async (HttpRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxImportBytes)
                return Results.Problem("Файл больше 1 МБ.", statusCode: StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxImportBytes)
                return Results.Problem("Файл больше 1 МБ.", statusCode: StatusCodes.Status413PayloadTooLarge);

            var bytes = buffer.ToArray();
            var products = await db.Products.AsNoTracking().ToListAsync(ct);
            IReadOnlyList<ImportRow> rows;
            try
            {
                // Файл Excel — это архив, он начинается с букв «PK»; всё остальное читается как CSV.
                rows = bytes is [(byte)'P', (byte)'K', ..]
                    ? SupplyImport.ParseRows(ReadExcel(bytes), products)
                    : SupplyImport.Parse(Decode(bytes), products);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.Problem("Файл не удалось прочитать. Подходит Excel (.xlsx) или CSV.", statusCode: StatusCodes.Status400BadRequest);
            }
            return rows.Count == 0
                ? Results.Problem("В файле нет строк с продуктами. Первая строка — заголовки колонок.", statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(rows);
        })
        .Produces<IReadOnlyList<ImportRow>>()
        .RequireRoles(Access.Stock);

        supplies.MapPost("", async (SupplyRequest req, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var createdAt = req.CreatedAt?.ToUniversalTime() ?? now;
            var (errors, products) = await CheckAsync(req, db, ct);
            if (errors.Check(createdAt <= now, "createdAt", "Дата поставки не может быть в будущем.").Any) return errors.ToResult();

            var supply = new Supply { Id = req.Id ?? Guid.NewGuid(), Number = req.Number!.Trim(), Supplier = Clean(req.Supplier), CreatedAt = createdAt };
            if (Fill(supply, req.Items!.Select(i => (products[i.ProductId], i.Quantity, i.ShelfLifeHours)), now) is { } expired)
                return errors.Add("items", expired).ToResult();
            if (await TakenAsync(db, supply.Id, supply.Number, ct))
                return Results.Problem("Поставка с таким идентификатором или номером уже принята.", statusCode: StatusCodes.Status409Conflict);

            Store(db, supply, now, user.Identity?.Name ?? "");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/supplies/{supply.Id}", ToResponse(supply));
        }).RequireRoles(Access.Stock);

        // Поставка из внешней системы — учётной программы или сервиса поставщика. Она ждёт в кабинете,
        // пока сотрудник склада её не подтвердит; до этого остатки не меняются.
        var incoming = supplies.MapGroup("/incoming").RequireRoles(Access.Stock);

        incoming.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            (await Waiting(db).Include(s => s.Items).ThenInclude(i => i.Product).OrderBy(s => s.ReceivedAt).ToListAsync(ct)).Select(ToResponse));

        incoming.MapPost("", async (SupplyRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var (errors, products) = await CheckAsync(req, db, ct);
            if (errors.Any) return errors.ToResult();

            var supply = new IncomingSupply
            {
                Id = req.Id ?? Guid.NewGuid(), Number = req.Number!.Trim(), Supplier = Clean(req.Supplier), ReceivedAt = time.GetUtcNow(),
                Items = req.Items!.Select(i => new IncomingItem { ProductId = i.ProductId, Product = products[i.ProductId], Quantity = i.Quantity, ShelfLifeHours = i.ShelfLifeHours }).ToList(),
            };
            if (await TakenAsync(db, supply.Id, supply.Number, ct)
                || await db.IncomingSupplies.AnyAsync(s => s.Id == supply.Id || (s.Number == supply.Number && s.Status == IncomingStatus.Pending), ct))
                return Results.Problem("Поставка с таким идентификатором или номером уже получена.", statusCode: StatusCodes.Status409Conflict);

            db.IncomingSupplies.Add(supply);
            db.Alerts.Add(new Alert
            {
                At = supply.ReceivedAt, Kind = AlertKind.SupplyWaiting,
                Title = $"Поставка {supply.Number} ждёт подтверждения",
                Detail = $"{supply.Supplier ?? "Поставщик не указан"} · позиций: {supply.Items.Count}",
            });
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/supplies/incoming/{supply.Id}", ToResponse(supply));
        }).Produces<IncomingResponse>(StatusCodes.Status202Accepted);

        // Приёмка: срок годности партий отсчитывается от этого момента, продукты появляются на складе.
        incoming.MapPost("{id:guid}/accept", async (Guid id, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var waiting = await Waiting(db).Include(s => s.Items).ThenInclude(i => i.Product).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (waiting is null) return Results.NotFound();
            if (await TakenAsync(db, waiting.Id, waiting.Number, ct))
                return Results.Problem($"Поставка с номером {waiting.Number} уже принята. Отклоните эту.", statusCode: StatusCodes.Status409Conflict);

            var now = time.GetUtcNow();
            var actor = user.Identity?.Name ?? "";
            var supply = new Supply { Id = waiting.Id, Number = waiting.Number, Supplier = waiting.Supplier, CreatedAt = now };
            Fill(supply, waiting.Items.Select(i => (i.Product!, i.Quantity, i.ShelfLifeHours)), now);
            Store(db, supply, now, actor);
            (waiting.Status, waiting.DecidedAt, waiting.DecidedBy) = (IncomingStatus.Accepted, now, actor);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(supply));
        }).Produces<SupplyResponse>();

        incoming.MapPost("{id:guid}/reject", async (Guid id, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var waiting = await Waiting(db).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (waiting is null) return Results.NotFound();
            (waiting.Status, waiting.DecidedAt, waiting.DecidedBy) = (IncomingStatus.Rejected, time.GetUtcNow(), user.Identity?.Name ?? "");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static IQueryable<IncomingSupply> Waiting(AppDbContext db) => db.IncomingSupplies.Where(s => s.Status == IncomingStatus.Pending);

    private static string? Clean(string? supplier) => string.IsNullOrWhiteSpace(supplier) ? null : supplier.Trim();

    private static Task<bool> TakenAsync(AppDbContext db, Guid id, string number, CancellationToken ct) =>
        db.Supplies.AnyAsync(s => s.Id == id || s.Number == number, ct);

    /// <summary>Общие правила для поставки, введённой в кабинете и присланной извне. Возвращает продукты поставки по идентификатору.</summary>
    private static async Task<(Errors Errors, Dictionary<Guid, Product> Products)> CheckAsync(SupplyRequest req, AppDbContext db, CancellationToken ct)
    {
        var items = req.Items ?? [];
        var errors = new Errors()
            .Check(!string.IsNullOrWhiteSpace(req.Number) && req.Number.Length <= 40, "number", "Укажите номер поставки до 40 символов.")
            .Check((req.Supplier?.Length ?? 0) <= 120, "supplier", "Название поставщика — до 120 символов.")
            .Check(items.Count > 0, "items", "В поставке должен быть хотя бы один продукт.")
            // Номера с такими началами выдаёт сама система: заготовкам, проверочным партиям и генератору.
            .Check(!(req.Number ?? "").TrimStart().StartsWith("З-") && !(req.Number ?? "").TrimStart().StartsWith("Т-") && !(req.Number ?? "").TrimStart().StartsWith("Г-"),
                "number", "Номера, которые начинаются на «З-», «Т-» и «Г-», заняты системой. Выберите другой.")
            .Check(items.Count <= 500, "items", "В поставке не больше 500 позиций.")
            .Check(items.All(i => i.Quantity is >= Validation.MinQuantity and <= 1_000_000), "items", "Количество каждого продукта — от 0,001 до 1 000 000.")
            .Check(items.All(i => i.ShelfLifeHours is null or (> 0 and <= 100_000)), "items", "Срок хранения — от 1 до 100 000 часов.");

        var ids = items.Select(i => i.ProductId).Distinct().ToArray();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        errors.Check(products.Count == ids.Length, "items", "В поставке есть неизвестный продукт.");
        return (errors, products);
    }

    /// <summary>Собирает партии поставки. Возвращает текст ошибки, если срок какой-то партии уже истёк.</summary>
    private static string? Fill(Supply supply, IEnumerable<(Product Product, decimal Quantity, int? ShelfLifeHours)> items, DateTimeOffset now)
    {
        foreach (var (product, quantity, life) in items)
        {
            var expiresAt = supply.CreatedAt.AddHours(life ?? product.ShelfLifeHours);
            if (expiresAt <= now) return $"{product.Name}: срок годности уже истёк, такую партию принять нельзя.";
            supply.Batches.Add(new Batch { Id = Guid.NewGuid(), ProductId = product.Id, Product = product, Quantity = quantity, Remaining = quantity, ExpiresAt = expiresAt });
        }
        return null;
    }

    private static void Store(AppDbContext db, Supply supply, DateTimeOffset now, string actor)
    {
        db.Supplies.Add(supply);
        db.Movements.AddRange(supply.Batches.Select(b => new Movement
        {
            At = now,
            Type = MovementType.Supply,
            ProductId = b.ProductId,
            BatchId = b.Id,
            Quantity = b.Quantity,
            Note = $"Поставка {supply.Number}",
            Actor = actor,
        }));
    }

    /// <summary>Первый лист книги Excel: каждая строка — массив значений ячеек.</summary>
    private static List<string[]> ReadExcel(byte[] bytes)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheets.First();
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        // Больше пяти тысяч строк в одной накладной не бывает; лишнее не читается.
        return sheet.RowsUsed().Take(SupplyImport.MaxRows + 1)
            .Select(row => Enumerable.Range(1, lastColumn)
                .Select(c => row.Cell(c).Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToArray())
            .ToList();
    }

    /// <summary>Читает файл как UTF-8, а если это не UTF-8 — как Windows-1251: так CSV сохраняет русский Excel.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static IncomingResponse ToResponse(IncomingSupply s) => new(
        s.Id, s.Number, s.Supplier, s.ReceivedAt, s.Items.Sum(i => i.Quantity * i.Product!.Cost),
        s.Items.Select(i => new IncomingItemResponse(i.ProductId, i.Product!.Name, i.Product.Unit, i.Quantity, i.ShelfLifeHours ?? i.Product.ShelfLifeHours)));

    private static SupplyResponse ToResponse(Supply s) => new(
        s.Id, s.Number, s.Supplier, s.CreatedAt, s.Batches.Sum(b => b.Quantity * b.Product!.Cost),
        s.Batches.Select(b => new SupplyBatchResponse(b.Id, b.ProductId, b.Product!.Name, b.Product.Unit, b.Quantity, b.Remaining, b.ExpiresAt)));
}
