using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record RecipeItemDto(Guid ProductId, decimal Quantity);

/// <param name="PrepRecipe">Состав на единицу продукта, если это заготовка; цена такой заготовки считается по составу.</param>
public sealed record ProductRequest(string? Name, Unit Unit, int ShelfLifeHours, decimal Cost, List<RecipeItemDto>? PrepRecipe = null);
public sealed record ProductResponse(Guid Id, string Name, Unit Unit, int ShelfLifeHours, decimal Cost, IEnumerable<RecipeItemDto> PrepRecipe);
public sealed record DishRequest(string? Name, decimal Price, List<RecipeItemDto>? Recipe, string? PhotoUrl = null, string? Category = null);
public sealed record DishResponse(
    Guid Id, string Name, decimal Price, string? PhotoUrl, IEnumerable<RecipeItemDto> Recipe, string? Category, bool ManuallyStopped);

public static class CatalogEndpoints
{
    public static void MapCatalog(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Справочник продуктов");

        products.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            (await db.Products.AsNoTracking().Include(p => p.PrepRecipe).OrderBy(p => p.Name).ToListAsync(ct)).Select(ToResponse));

        products.MapPost("", async (ProductRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (await ValidateAsync(req, null, db, ct) is { Any: true } errors) return errors.ToResult();
            if (await db.Products.AnyAsync(p => p.Name == req.Name, ct)) return NameTaken("Продукт");

            var product = new Product { Name = req.Name!.Trim(), Unit = req.Unit, ShelfLifeHours = req.ShelfLifeHours, Cost = req.Cost };
            await SetPrepRecipeAsync(product, req, db, ct);
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/products/{product.Id}", ToResponse(product));
        }).RequireRoles(Access.Catalog);

        products.MapPut("{id:guid}", async (Guid id, ProductRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (await ValidateAsync(req, id, db, ct) is { Any: true } errors) return errors.ToResult();
            var product = await db.Products.Include(p => p.PrepRecipe).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return Results.NotFound();
            if (await db.Products.AnyAsync(p => p.Name == req.Name && p.Id != id, ct)) return NameTaken("Продукт");

            (product.Name, product.Unit, product.ShelfLifeHours, product.Cost) = (req.Name!.Trim(), req.Unit, req.ShelfLifeHours, req.Cost);
            await SetPrepRecipeAsync(product, req, db, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(product));
        }).RequireRoles(Access.Catalog);

        products.MapDelete("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var used = await db.Batches.AnyAsync(b => b.ProductId == id, ct)
                || await db.Dishes.AnyAsync(d => d.Recipe.Any(r => r.ProductId == id), ct)
                || await db.Products.AnyAsync(p => p.PrepRecipe.Any(i => i.ProductId == id), ct);
            if (used)
                return Results.Problem("Продукт используется в поставках или техкартах, удалить его нельзя.", statusCode: StatusCodes.Status409Conflict);
            var deleted = await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireRoles(Access.Catalog);

        var dishes = api.MapGroup("/dishes").WithTags("Блюда и техкарты");

        dishes.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            (await db.Dishes.Include(d => d.Recipe).OrderBy(d => d.Name).ToListAsync(ct)).Select(ToResponse));

        dishes.MapPost("", async (DishRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (await ValidateAsync(req, db, ct) is { Any: true } errors) return errors.ToResult();
            if (await db.Dishes.AnyAsync(d => d.Name == req.Name, ct)) return NameTaken("Блюдо");

            var dish = new Dish { Name = req.Name!.Trim(), Price = req.Price, PhotoUrl = req.PhotoUrl, Category = Category(req), Recipe = ToRecipe(req) };
            db.Dishes.Add(dish);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/dishes/{dish.Id}", ToResponse(dish));
        }).RequireRoles(Access.Catalog);

        dishes.MapPut("{id:guid}", async (Guid id, DishRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (await ValidateAsync(req, db, ct) is { Any: true } errors) return errors.ToResult();
            var dish = await db.Dishes.Include(d => d.Recipe).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (dish is null) return Results.NotFound();
            if (await db.Dishes.AnyAsync(d => d.Name == req.Name && d.Id != id, ct)) return NameTaken("Блюдо");

            // Фото меняется отдельной загрузкой; запрос без ссылки его не стирает.
            (dish.Name, dish.Price, dish.PhotoUrl, dish.Category, dish.Recipe) =
                (req.Name!.Trim(), req.Price, req.PhotoUrl ?? dish.PhotoUrl, Category(req), ToRecipe(req));
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(dish));
        }).RequireRoles(Access.Catalog);

        dishes.MapDelete("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.Orders.AnyAsync(o => o.Items.Any(i => i.DishId == id), ct))
                return Results.Problem("По блюду уже есть заказы, удалить его нельзя.", statusCode: StatusCodes.Status409Conflict);
            var deleted = await db.Dishes.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireRoles(Access.Catalog);

        // Снять блюдо с продажи вручную и вернуть: стоп-лист при этом не ждёт, пока закончатся продукты.
        dishes.MapPost("{id:guid}/stop", (Guid id, AppDbContext db, EventBus bus, CancellationToken ct) => SetStopped(id, true, db, bus, ct)).RequireRoles(Access.Orders);
        dishes.MapPost("{id:guid}/resume", (Guid id, AppDbContext db, EventBus bus, CancellationToken ct) => SetStopped(id, false, db, bus, ct)).RequireRoles(Access.Orders);

        // Фото отдаётся без входа: браузер запрашивает картинки без токена, а секрета в них нет.
        dishes.MapGet("{id:guid}/photo", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.DishPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.DishId == id, ct) is { } photo
                ? Results.File(photo.Data, photo.ContentType)
                : Results.NotFound())
        .AllowAnonymous();

        dishes.MapPut("{id:guid}/photo", async (Guid id, HttpRequest request, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var contentType = request.ContentType ?? "";
            if (contentType is not ("image/jpeg" or "image/png" or "image/webp"))
                return Results.Problem("Подходит фото в формате JPG, PNG или WebP.", statusCode: StatusCodes.Status415UnsupportedMediaType);
            if (request.ContentLength > MaxPhotoBytes)
                return Results.Problem("Фото больше 10 МБ.", statusCode: StatusCodes.Status413PayloadTooLarge);

            var dish = await db.Dishes.FindAsync([id], ct);
            if (dish is null) return Results.NotFound();

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length is 0 or > MaxPhotoBytes)
                return Results.Problem("Фото пустое или больше 10 МБ.", statusCode: StatusCodes.Status413PayloadTooLarge);

            var photo = await db.DishPhotos.FindAsync([id], ct);
            if (photo is null) db.DishPhotos.Add(new DishPhoto { DishId = id, ContentType = contentType, Data = buffer.ToArray() });
            else (photo.ContentType, photo.Data) = (contentType, buffer.ToArray());
            // Метка времени в адресе заставляет браузер показать новое фото, а не сохранённое старое.
            dish.PhotoUrl = $"/api/dishes/{id}/photo?v={time.GetUtcNow().ToUnixTimeSeconds()}";
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { dish.PhotoUrl });
        }).RequireRoles(Access.Catalog);
    }

    private const int MaxPhotoBytes = 10_000_000;

    private static async Task<IResult> SetStopped(Guid id, bool stopped, AppDbContext db, EventBus bus, CancellationToken ct)
    {
        var updated = await db.Dishes.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ManuallyStopped, stopped), ct);
        if (updated > 0) bus.Publish();
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static string? Category(DishRequest req) => string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();

    private static Errors Validate(ProductRequest req) => new Errors()
        .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Length <= 120, "name", "Укажите название до 120 символов.")
        .Check(Enum.IsDefined(req.Unit), "unit", "Единица измерения: Kg, L или Pcs.")
        .Check(req.ShelfLifeHours > 0, "shelfLifeHours", "Срок хранения должен быть больше нуля.")
        .Check(req.Cost >= 0, "cost", "Цена не может быть отрицательной.");

    private static async Task<Errors> ValidateAsync(ProductRequest req, Guid? id, AppDbContext db, CancellationToken ct)
    {
        var errors = Validate(req);
        var recipe = req.PrepRecipe ?? [];
        if (recipe.Count == 0) return errors;

        var ids = recipe.Select(r => r.ProductId).ToArray();
        errors
            .Check(recipe.All(r => r.Quantity > 0), "prepRecipe", "Количество каждого продукта должно быть больше нуля.")
            .Check(ids.Distinct().Count() == ids.Length, "prepRecipe", "Продукт указан в составе дважды.")
            .Check(id is null || !ids.Contains(id.Value), "prepRecipe", "Заготовка не может входить в собственный состав.");
        var known = await db.Products.Where(p => ids.Contains(p.Id)).Select(p => new { IsPrep = p.PrepRecipe.Any() }).ToListAsync(ct);
        return errors
            .Check(known.Count == ids.Distinct().Count(), "prepRecipe", "В составе есть неизвестный продукт.")
            // Заготовка из заготовки не поддерживается: состав — только покупные продукты.
            .Check(known.All(p => !p.IsPrep), "prepRecipe", "В состав заготовки нельзя включать другую заготовку.")
            .Check(id is null || !await db.Products.AnyAsync(p => p.PrepRecipe.Any(i => i.ProductId == id), ct), "prepRecipe", "Этот продукт сам входит в состав заготовки.");
    }

    /// <summary>Записывает состав заготовки и пересчитывает её цену по составу.</summary>
    private static async Task SetPrepRecipeAsync(Product product, ProductRequest req, AppDbContext db, CancellationToken ct)
    {
        var recipe = req.PrepRecipe ?? [];
        product.PrepRecipe.Clear();
        product.PrepRecipe.AddRange(recipe.Select(r => new PrepItem { ProductId = r.ProductId, Quantity = r.Quantity }));
        if (recipe.Count == 0) return;

        var ids = recipe.Select(r => r.ProductId).ToArray();
        var costs = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Cost, ct);
        product.Cost = Math.Round(recipe.Sum(r => r.Quantity * costs[r.ProductId]), 2);
    }

    private static async Task<Errors> ValidateAsync(DishRequest req, AppDbContext db, CancellationToken ct)
    {
        var errors = new Errors()
            .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Length <= 120, "name", "Укажите название до 120 символов.")
            .Check(req.Price >= 0, "price", "Цена не может быть отрицательной.")
            .Check(req.PhotoUrl is null or { Length: <= 300 }, "photoUrl", "Ссылка на фото — до 300 символов.")
            .Check((req.Category?.Length ?? 0) <= 60, "category", "Раздел меню — до 60 символов.");

        var recipe = req.Recipe ?? [];
        if (recipe.Count == 0) return errors.Add("recipe", "В составе блюда должен быть хотя бы один продукт.");

        errors
            .Check(recipe.All(r => r.Quantity > 0), "recipe", "Количество каждого продукта должно быть больше нуля.")
            .Check(recipe.Select(r => r.ProductId).Distinct().Count() == recipe.Count, "recipe", "Продукт указан в составе дважды.");

        var ids = recipe.Select(r => r.ProductId).ToArray();
        var known = await db.Products.CountAsync(p => ids.Contains(p.Id), ct);
        return errors.Check(known == ids.Distinct().Count(), "recipe", "В составе есть неизвестный продукт.");
    }

    private static List<RecipeItem> ToRecipe(DishRequest req) =>
        req.Recipe!.Select(r => new RecipeItem { ProductId = r.ProductId, Quantity = r.Quantity }).ToList();

    private static ProductResponse ToResponse(Product p) =>
        new(p.Id, p.Name, p.Unit, p.ShelfLifeHours, p.Cost, p.PrepRecipe.Select(i => new RecipeItemDto(i.ProductId, i.Quantity)));

    private static DishResponse ToResponse(Dish d) =>
        new(d.Id, d.Name, d.Price, d.PhotoUrl, d.Recipe.Select(r => new RecipeItemDto(r.ProductId, r.Quantity)), d.Category, d.ManuallyStopped);

    private static IResult NameTaken(string what) =>
        Results.Problem($"{what} с таким названием уже есть.", statusCode: StatusCodes.Status409Conflict);
}
