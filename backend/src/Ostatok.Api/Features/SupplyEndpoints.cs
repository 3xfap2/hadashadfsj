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
            var items = req.Items ?? [];

            var errors = new Errors()
                .Check(!string.IsNullOrWhiteSpace(req.Number) && req.Number.Length <= 40, "number", "Укажите номер поставки до 40 символов.")
                .Check((req.Supplier?.Length ?? 0) <= 120, "supplier", "Название поставщика — до 120 символов.")
                .Check(createdAt <= now, "createdAt", "Дата поставки не может быть в будущем.")
                .Check(items.Count > 0, "items", "В поставке должен быть хотя бы один продукт.")
                .Check(items.All(i => i.Quantity > 0), "items", "Количество каждого продукта должно быть больше нуля.")
                .Check(items.All(i => i.ShelfLifeHours is null or > 0), "items", "Срок хранения должен быть больше нуля.");

            var ids = items.Select(i => i.ProductId).Distinct().ToArray();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            errors.Check(products.Count == ids.Length, "items", "В поставке есть неизвестный продукт.");
            if (errors.Any) return errors.ToResult();

            var supply = new Supply
            {
                Id = req.Id ?? Guid.NewGuid(),
                Number = req.Number!.Trim(),
                Supplier = string.IsNullOrWhiteSpace(req.Supplier) ? null : req.Supplier.Trim(),
                CreatedAt = createdAt,
            };
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
                Actor = user.Identity?.Name ?? "",
            }));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/supplies/{supply.Id}", ToResponse(supply));
        }).RequireRoles(Access.Stock);
    }

    /// <summary>Первый лист книги Excel: каждая строка — массив значений ячеек.</summary>
    private static List<string[]> ReadExcel(byte[] bytes)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheets.First();
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        return sheet.RowsUsed()
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

    private static SupplyResponse ToResponse(Supply s) => new(
        s.Id, s.Number, s.Supplier, s.CreatedAt, s.Batches.Sum(b => b.Quantity * b.Product!.Cost),
        s.Batches.Select(b => new SupplyBatchResponse(b.Id, b.ProductId, b.Product!.Name, b.Product.Unit, b.Quantity, b.Remaining, b.ExpiresAt)));
}
