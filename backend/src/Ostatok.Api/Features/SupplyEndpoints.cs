using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="ShelfLifeHours">Срок хранения этой партии; если не указан, берётся норматив продукта.</param>
/// <param name="Cost">
/// Цена за единицу из файла поставки. Если указана, после приёмки становится закупочной ценой продукта;
/// при ручном вводе не передаётся, и цена в справочнике остаётся прежней.
/// </param>
/// <param name="ProductName">
/// Для поставки извне: продукт можно указать точным названием из справочника ресторана вместо ProductId
/// (ProductId тогда не передаётся). Регистр букв не важен.
/// </param>
/// <param name="ExpiresAt">Для поставки извне: «годен до» вместо срока хранения в часах.</param>
public sealed record SupplyItemRequest(Guid ProductId, decimal Quantity, int? ShelfLifeHours, decimal? Cost = null, string? ProductName = null, DateTimeOffset? ExpiresAt = null);

/// <param name="Id">Идентификатор поставки во внешней системе; повторная отправка с тем же Id не создаст дубль.</param>
/// <param name="CreatedAt">Дата создания поставки; по умолчанию — текущее время.</param>
/// <param name="SupplierInn">ИНН поставщика: 10 или 12 цифр. Обязателен, когда поставку присылает внешняя система по ключу.</param>
public sealed record SupplyRequest(Guid? Id, string? Number, DateTimeOffset? CreatedAt, List<SupplyItemRequest>? Items, string? Supplier = null, string? SupplierInn = null);

public sealed record SupplyBatchResponse(Guid BatchId, Guid ProductId, string ProductName, Unit Unit, decimal Quantity, decimal Remaining, DateTimeOffset ExpiresAt);
/// <param name="Total">Стоимость поставки по закупочным ценам из справочника; пусто для ролей, которым цены не видны.</param>
public sealed record SupplyResponse(Guid Id, string Number, string? Supplier, DateTimeOffset CreatedAt, decimal? Total, IEnumerable<SupplyBatchResponse> Items);

/// <param name="Id">Строка поставки: по ней указывается принятое количество при частичной приёмке.</param>
/// <param name="Quantity">Сколько заказано (прислал поставщик).</param>
/// <param name="Accepted">Сколько принято; пусто, пока поставка ждёт решения или если её отклонили.</param>
public sealed record IncomingItemResponse(Guid ProductId, string ProductName, Unit Unit, decimal Quantity, int ShelfLifeHours, long Id = 0, decimal? Accepted = null);
/// <summary>Поставка от внешней системы: ждёт подтверждения или уже решена (в истории).</summary>
/// <param name="CreatedAt">Дата создания поставки: от неё считается срок годности партий.</param>
public sealed record IncomingResponse(
    Guid Id, string Number, string? Supplier, DateTimeOffset ReceivedAt, decimal Total, IEnumerable<IncomingItemResponse> Items, DateTimeOffset CreatedAt,
    string? SupplierInn = null, IncomingStatus Status = IncomingStatus.Pending, DateTimeOffset? DecidedAt = null, string? DecidedBy = null);

/// <param name="Id">Строка поставки (IncomingItemResponse.Id).</param>
/// <param name="Accepted">Сколько принято: от 0 до заказанного; 0 — строка не принята.</param>
public sealed record AcceptLine(long Id, decimal Accepted);
/// <param name="Items">Принятое количество по строкам. Строки, которых нет в списке, принимаются целиком; без тела принимается вся поставка.</param>
public sealed record AcceptIncomingRequest(List<AcceptLine>? Items);

public static class SupplyEndpoints
{
    private const int MaxImportBytes = 1_000_000;

    public static void MapSupplies(this RouteGroupBuilder api)
    {
        var supplies = api.MapGroup("/supplies").WithTags("Поставки");

        supplies.MapGet("", async (AppDbContext db, ClaimsPrincipal user, CancellationToken ct, int take = 50) =>
            (await db.Supplies
                .Include(s => s.Batches).ThenInclude(b => b.Product)
                .OrderByDescending(s => s.CreatedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(ct))
            .Select(s => ToResponse(s, user.SeesCosts())));

        // Предпросмотр файла поставки: тело запроса — сам файл CSV. Ничего не сохраняет;
        // после подтверждения строки без ошибок отправляются обычным POST /api/supplies вместе с ценой из файла.
        supplies.MapPost("/import/preview", async (HttpRequest request, AppDbContext db, IInvoiceReader reader, PhotoQuota quota, TimeProvider time, StockOptions options, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxImportBytes)
                return Results.Problem("Файл больше 1 МБ.", statusCode: StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxImportBytes)
                return Results.Problem("Файл больше 1 МБ.", statusCode: StatusCodes.Status413PayloadTooLarge);

            var bytes = buffer.ToArray();
            var products = await db.Products.AsNoTracking().Include(p => p.PrepRecipe).ToListAsync(ct);
            IReadOnlyList<ImportRow> rows;
            // Фото накладной или этикетки: модель переписывает его в ту же таблицу, что и файл, дальше — обычная проверка по справочнику.
            if (ImageType(bytes) is { } mime)
            {
                if (!reader.Enabled)
                    return Results.Problem("Распознавание по фото на этом сервере не настроено. Загрузите файл Excel или CSV.", statusCode: StatusCodes.Status503ServiceUnavailable);
                if (!quota.TryTake(db.TenantId))
                    return Results.Problem($"Распознано уже {PhotoQuota.PerHour} снимков за час. Подождите или загрузите файл.", statusCode: StatusCodes.Status429TooManyRequests);
                try
                {
                    rows = SupplyImport.ParseRows(await reader.ReadAsync(bytes, mime, products, time.GetUtcNow().ToOffset(TimeSpan.FromHours(options.UtcOffsetHours)), ct), products);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logs.CreateLogger("Ostatok.InvoiceReader").LogWarning(e, "Фото поставки не распознано");
                    return Results.Problem("Фото не удалось распознать. Попробуйте ещё раз или загрузите файл.", statusCode: StatusCodes.Status502BadGateway);
                }
                return rows.Count == 0
                    ? Results.Problem("На фото не нашлось строк с продуктами. Снимите накладную ближе и ровнее.", statusCode: StatusCodes.Status400BadRequest)
                    : Results.Ok(rows);
            }
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
            (req, var unknown) = await ResolveAsync(req, createdAt, db, ct);
            var (errors, products) = await CheckAsync(req, db, ct);
            foreach (var message in unknown) errors.Add("items", message);
            if (errors.Check(createdAt <= now, "createdAt", "Дата поставки не может быть в будущем.").Any) return errors.ToResult();

            var supply = new Supply { Id = req.Id ?? Guid.NewGuid(), Number = req.Number!.Trim(), Supplier = Clean(req.Supplier), CreatedAt = createdAt };
            // В партию записывается цена этой поставки; если её не прислали — текущая цена из справочника.
            decimal UnitCost(SupplyItemRequest i) =>
                i.Cost is null || products[i.ProductId].PrepRecipe.Count > 0 ? products[i.ProductId].Cost : Math.Round(i.Cost.Value, 2);
            if (Fill(supply, req.Items!.Select(i => (products[i.ProductId], i.Quantity, i.ShelfLifeHours, UnitCost(i))), now) is { } expired)
                return errors.Add("items", expired).ToResult();
            if (await TakenAsync(db, supply.Id, supply.Number, ct))
                return Results.Problem("Поставка с таким идентификатором или номером уже принята.", statusCode: StatusCodes.Status409Conflict);

            // Цена из файла поставки становится закупочной ценой продукта. У заготовки цена своя — по составу.
            // Поставка, внесённая задним числом, цену в справочнике не трогает: там должна остаться последняя.
            var priced = req.Items!.Where(i => i.Cost is not null && products[i.ProductId].PrepRecipe.Count == 0 && createdAt >= now.AddDays(-1)).ToList();
            foreach (var item in priced) products[item.ProductId].Cost = Math.Round(item.Cost!.Value, 2);
            if (priced.Count > 0) await db.RecalcPrepCostsAsync(priced.Select(i => i.ProductId).ToArray(), ct);

            Store(db, supply, now, user.Identity?.Name ?? "");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/supplies/{supply.Id}", ToResponse(supply));
        }).RequireRoles(Access.Stock);

        // Поставка из внешней системы — учётной программы или сервиса поставщика. Она ждёт в кабинете,
        // пока сотрудник склада её не подтвердит; до этого остатки не меняются.
        var incoming = supplies.MapGroup("/incoming").RequireRoles(Access.Stock);

        incoming.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            (await Waiting(db).Include(s => s.Items).ThenInclude(i => i.Product).OrderBy(s => s.ReceivedAt).ToListAsync(ct)).Select(ToResponse));

        // История решений: принятые, принятые частично и отклонённые поставки. Отклонённая остаётся в базе, на склад она ничего не добавила.
        incoming.MapGet("/history", async (AppDbContext db, CancellationToken ct, int take = 50) =>
            (await db.IncomingSupplies.Where(s => s.Status != IncomingStatus.Pending)
                .Include(s => s.Items).ThenInclude(i => i.Product)
                .OrderByDescending(s => s.DecidedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(ct))
            .Select(ToResponse));

        // Присылает сотрудник склада (кабинет, бот — по своему входу) или внешняя система по ключу интеграции ресторана
        // в заголовке X-Api-Key. Ключ открывает только этот метод и только для своего ресторана: ресторан определяется ключом.
        incoming.MapPost("", async (SupplyRequest req, HttpContext http, Tenant tenant, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var byKey = http.User.Identity?.IsAuthenticated != true;
            if (byKey)
            {
                if (await IntegrationKey.FindRestaurantAsync(db, http.Request.Headers[IntegrationKey.Header], ct) is not { } restaurant)
                    return Results.Problem($"Нужен действующий ключ интеграции ресторана в заголовке {IntegrationKey.Header}. Ключ выдаёт управляющий в разделе «Ресторан».",
                        statusCode: StatusCodes.Status401Unauthorized);
                tenant.Id = restaurant;
            }
            else if (!Access.Stock.Any(r => http.User.IsInRole(r.ToString()))) return Results.Forbid();

            var now = time.GetUtcNow();
            var createdAt = req.CreatedAt?.ToUniversalTime() ?? now;
            (req, var unknown) = await ResolveAsync(req, createdAt, db, ct);
            var (errors, products) = await CheckAsync(req, db, ct);
            foreach (var message in unknown) errors.Add("items", message);
            errors.Check(createdAt <= now, "createdAt", "Дата поставки не может быть в будущем.")
                .Check(!byKey || !string.IsNullOrWhiteSpace(req.Supplier), "supplier", "Укажите название поставщика.")
                .Check(!byKey || req.SupplierInn is not null, "supplierInn", "Укажите ИНН поставщика: 10 или 12 цифр.");
            if (errors.Any) return errors.ToResult();

            var supply = new IncomingSupply
            {
                Id = req.Id ?? Guid.NewGuid(), Number = req.Number!.Trim(), Supplier = Clean(req.Supplier), SupplierInn = req.SupplierInn?.Trim(), ReceivedAt = now, CreatedAt = createdAt,
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
            // Внешней системе — только квитанция: закупочные цены и справочник ресторана ей не показываются.
            return byKey
                ? Results.Accepted($"/api/supplies/incoming/{supply.Id}", new { supply.Id, supply.Number, supply.Status, Items = supply.Items.Count })
                : Results.Accepted($"/api/supplies/incoming/{supply.Id}", ToResponse(supply));
        })
        .AllowAnonymous()
        .Produces<IncomingResponse>(StatusCodes.Status202Accepted);

        // Приёмка: продукты появляются на складе. Срок годности партий отсчитывается от даты создания поставки,
        // как у поставки, внесённой в кабинете: пока она ждала подтверждения, продукт старел.
        // У поставок, полученных до появления этой даты, срок по-прежнему считается от приёмки.
        // Частичная приёмка: по строкам указано, сколько принято (0 — строка не принята); на склад встаёт только принятое.
        incoming.MapPost("{id:guid}/accept", async (Guid id, AcceptIncomingRequest? req, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await LockWaitingAsync(db, id, ct)) return Results.NotFound();
            var waiting = await Waiting(db).Include(s => s.Items).ThenInclude(i => i.Product).FirstAsync(s => s.Id == id, ct);
            if (await TakenAsync(db, waiting.Id, waiting.Number, ct))
                return Results.Problem($"Поставка с номером {waiting.Number} уже принята. Отклоните эту.", statusCode: StatusCodes.Status409Conflict);

            var lines = (req?.Items ?? []).GroupBy(l => l.Id).ToDictionary(g => g.Key, g => Math.Round(g.Last().Accepted, 3));
            foreach (var item in waiting.Items) item.Accepted = lines.GetValueOrDefault(item.Id, item.Quantity);
            var errors = new Errors()
                .Check(lines.Keys.All(k => waiting.Items.Any(i => i.Id == k)), "items", "В приёмке есть строка не из этой поставки.")
                .Check(waiting.Items.All(i => i.Accepted >= 0 && i.Accepted <= i.Quantity), "items", "Принятое количество — от 0 до заказанного.")
                .Check(waiting.Items.Any(i => i.Accepted > 0), "items", "Ничего не принято. Если поставку не берёте, отклоните её.");
            if (errors.Any) return errors.ToResult();

            var now = time.GetUtcNow();
            var actor = user.Identity?.Name ?? "";
            var supply = new Supply { Id = waiting.Id, Number = waiting.Number, Supplier = waiting.Supplier, CreatedAt = waiting.CreatedAt ?? now };
            if (Fill(supply, waiting.Items.Where(i => i.Accepted > 0).Select(i => (i.Product!, i.Accepted!.Value, i.ShelfLifeHours, i.Product!.Cost)), now) is { } expired)
                return Results.Problem($"{expired} Отклоните поставку или не принимайте эту строку.", statusCode: StatusCodes.Status409Conflict);
            Store(db, supply, now, actor);
            var status = waiting.Items.All(i => i.Accepted == i.Quantity) ? IncomingStatus.Accepted : IncomingStatus.Partial;
            (waiting.Status, waiting.DecidedAt, waiting.DecidedBy) = (status, now, actor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToResponse(supply));
        }).Produces<SupplyResponse>();

        incoming.MapPost("{id:guid}/reject", async (Guid id, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await LockWaitingAsync(db, id, ct)) return Results.NotFound();
            var waiting = await Waiting(db).FirstAsync(s => s.Id == id, ct);
            (waiting.Status, waiting.DecidedAt, waiting.DecidedBy) = (IncomingStatus.Rejected, time.GetUtcNow(), user.Identity?.Name ?? "");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    private static IQueryable<IncomingSupply> Waiting(AppDbContext db) => db.IncomingSupplies.Where(s => s.Status == IncomingStatus.Pending);

    /// <summary>Блокирует ожидающую поставку до конца транзакции: принять и отклонить её одновременно нельзя. false — такой нет или она уже решена.</summary>
    private static async Task<bool> LockWaitingAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "IncomingSupplies"
            WHERE "Id" = {id} AND "RestaurantId" = {db.TenantId} AND "Status" = {nameof(IncomingStatus.Pending)}
            FOR UPDATE
            """).ToListAsync(ct)).Count > 0;

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
            .Check(req.SupplierInn is null || req.SupplierInn.Trim() is { Length: 10 or 12 } inn && inn.All(char.IsAsciiDigit), "supplierInn", "ИНН поставщика — 10 цифр у организации или 12 у предпринимателя.")
            .Check(items.Count > 0, "items", "В поставке должен быть хотя бы один продукт.")
            // Номера с такими началами выдаёт сама система: заготовкам, проверочным партиям и генератору.
            .Check(!(req.Number ?? "").TrimStart().StartsWith("З-") && !(req.Number ?? "").TrimStart().StartsWith("Т-") && !(req.Number ?? "").TrimStart().StartsWith("Г-"),
                "number", "Номера, которые начинаются на «З-», «Т-» и «Г-», заняты системой. Выберите другой.")
            .Check(items.Count <= 500, "items", "В поставке не больше 500 позиций.")
            .Check(items.All(i => i.Quantity is >= Validation.MinQuantity and <= 1_000_000), "items", "Количество каждого продукта — от 0,001 до 1 000 000.")
            .Check(items.All(i => i.ShelfLifeHours is null or (> 0 and <= 100_000)), "items", "Срок хранения — от 1 до 100 000 часов.")
            .Check(items.All(i => i.Cost is null || Math.Round(i.Cost.Value, 2) is > 0 and <= 10_000_000), "items", "Цена продукта — от 1 копейки до 10 000 000 ₽.");

        // Строка с названием, которого нет в справочнике, уже получила свою ошибку в ResolveAsync.
        var ids = items.Select(i => i.ProductId).Where(id => id != Guid.Empty).Distinct().ToArray();
        var products = await db.Products.Include(p => p.PrepRecipe).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        errors.Check(products.Count == ids.Length, "items", "В поставке есть неизвестный продукт: такого productId нет в справочнике ресторана.")
            .Check(items.All(i => i.ProductId != Guid.Empty || !string.IsNullOrWhiteSpace(i.ProductName)), "items", "Укажите продукт каждой строки: productId или productName.");
        return (errors, products);
    }

    /// <summary>
    /// Поставка извне: продукт по точному названию из справочника вместо идентификатора, «годен до» вместо срока в часах.
    /// Возвращает запрос с подставленными идентификаторами и сроками и тексты ошибок по названиям, которых нет в справочнике.
    /// </summary>
    private static async Task<(SupplyRequest Request, List<string> Unknown)> ResolveAsync(SupplyRequest req, DateTimeOffset createdAt, AppDbContext db, CancellationToken ct)
    {
        var unknown = new List<string>();
        if (req.Items is null) return (req, unknown);
        var byName = req.Items.Any(i => i.ProductId == Guid.Empty && !string.IsNullOrWhiteSpace(i.ProductName))
            ? (await db.Products.Select(p => new { p.Id, p.Name }).ToListAsync(ct))
                .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase)
            : [];
        var items = req.Items.Select(i =>
        {
            if (i.ProductId == Guid.Empty && !string.IsNullOrWhiteSpace(i.ProductName))
            {
                if (byName.TryGetValue(i.ProductName.Trim(), out var id)) i = i with { ProductId = id };
                else unknown.Add($"Продукта «{i.ProductName.Trim()}» нет в справочнике ресторана. Укажите название точно как в справочнике.");
            }
            if (i.ExpiresAt is { } expiresAt)
            {
                var hours = (int)Math.Floor((expiresAt - createdAt).TotalHours);
                if (hours < 1) unknown.Add($"«Годен до» должен быть хотя бы на час позже даты поставки ({i.ProductName ?? i.ProductId.ToString()}).");
                else i = i with { ShelfLifeHours = hours };
            }
            return i;
        }).ToList();
        return (req with { Items = items }, unknown);
    }

    /// <summary>Собирает партии поставки. Возвращает текст ошибки, если срок какой-то партии уже истёк.</summary>
    private static string? Fill(Supply supply, IEnumerable<(Product Product, decimal Quantity, int? ShelfLifeHours, decimal UnitCost)> items, DateTimeOffset now)
    {
        foreach (var (product, quantity, life, cost) in items)
        {
            var expiresAt = supply.CreatedAt.AddHours(life ?? product.ShelfLifeHours);
            if (expiresAt <= now) return $"{product.Name}: срок годности уже истёк, такую партию принять нельзя.";
            supply.Batches.Add(new Batch { Id = Guid.NewGuid(), ProductId = product.Id, Product = product, Quantity = quantity, Remaining = quantity, ExpiresAt = expiresAt, UnitCost = cost });
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

    /// <summary>Тип картинки по первым байтам файла; null — это не фото.</summary>
    private static string? ImageType(byte[] bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null,
    };

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
        s.Items.OrderBy(i => i.Id).Select(i => new IncomingItemResponse(i.ProductId, i.Product!.Name, i.Product.Unit, i.Quantity, i.ShelfLifeHours ?? i.Product.ShelfLifeHours, i.Id, i.Accepted)),
        s.CreatedAt ?? s.ReceivedAt, s.SupplierInn, s.Status, s.DecidedAt, s.DecidedBy);

    private static SupplyResponse ToResponse(Supply s, bool costs = true) => new(
        s.Id, s.Number, s.Supplier, s.CreatedAt, costs ? s.Batches.Sum(b => b.Quantity * b.UnitCost) : null,
        s.Batches.Select(b => new SupplyBatchResponse(b.Id, b.ProductId, b.Product!.Name, b.Product.Unit, b.Quantity, b.Remaining, b.ExpiresAt)));
}
