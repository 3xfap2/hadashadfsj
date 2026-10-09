using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record ProductRequest(string? Name, Unit Unit, int ShelfLifeHours, decimal Cost);
public sealed record ProductResponse(Guid Id, string Name, Unit Unit, int ShelfLifeHours, decimal Cost);

public sealed record RecipeItemDto(Guid ProductId, decimal Quantity);
public sealed record DishRequest(string? Name, decimal Price, List<RecipeItemDto>? Recipe, string? PhotoUrl = null);
public sealed record DishResponse(Guid Id, string Name, decimal Price, string? PhotoUrl, IEnumerable<RecipeItemDto> Recipe);

public static class CatalogEndpoints
{
    public static void MapCatalog(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Справочник продуктов");

        products.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            await db.Products.OrderBy(p => p.Name).Select(p => ToResponse(p)).ToListAsync(ct));

        products.MapPost("", async (ProductRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(req) is { Any: true } errors) return errors.ToResult();
            if (await db.Products.AnyAsync(p => p.Name == req.Name, ct)) return NameTaken("Продукт");

            var product = new Product { Name = req.Name!.Trim(), Unit = req.Unit, ShelfLifeHours = req.ShelfLifeHours, Cost = req.Cost };
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/products/{product.Id}", ToResponse(product));
        }).RequireRoles(Access.Catalog);

        products.MapPut("{id:guid}", async (Guid id, ProductRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(req) is { Any: true } errors) return errors.ToResult();
            var product = await db.Products.FindAsync([id], ct);
            if (product is null) return Results.NotFound();
            if (await db.Products.AnyAsync(p => p.Name == req.Name && p.Id != id, ct)) return NameTaken("Продукт");

            (product.Name, product.Unit, product.ShelfLifeHours, product.Cost) = (req.Name!.Trim(), req.Unit, req.ShelfLifeHours, req.Cost);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(product));
        }).RequireRoles(Access.Catalog);

        products.MapDelete("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var used = await db.Batches.AnyAsync(b => b.ProductId == id, ct)
                || await db.Dishes.AnyAsync(d => d.Recipe.Any(r => r.ProductId == id), ct);
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

            var dish = new Dish { Name = req.Name!.Trim(), Price = req.Price, PhotoUrl = req.PhotoUrl, Recipe = ToRecipe(req) };
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

            (dish.Name, dish.Price, dish.PhotoUrl, dish.Recipe) = (req.Name!.Trim(), req.Price, req.PhotoUrl, ToRecipe(req));
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
    }

    private static Errors Validate(ProductRequest req) => new Errors()
        .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Length <= 120, "name", "Укажите название до 120 символов.")
        .Check(Enum.IsDefined(req.Unit), "unit", "Единица измерения: Kg, L или Pcs.")
        .Check(req.ShelfLifeHours > 0, "shelfLifeHours", "Срок хранения должен быть больше нуля.")
        .Check(req.Cost >= 0, "cost", "Цена не может быть отрицательной.");

    private static async Task<Errors> ValidateAsync(DishRequest req, AppDbContext db, CancellationToken ct)
    {
        var errors = new Errors()
            .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Length <= 120, "name", "Укажите название до 120 символов.")
            .Check(req.Price >= 0, "price", "Цена не может быть отрицательной.")
            .Check(req.PhotoUrl is null or { Length: <= 300 }, "photoUrl", "Ссылка на фото — до 300 символов.");

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

    private static ProductResponse ToResponse(Product p) => new(p.Id, p.Name, p.Unit, p.ShelfLifeHours, p.Cost);

    private static DishResponse ToResponse(Dish d) =>
        new(d.Id, d.Name, d.Price, d.PhotoUrl, d.Recipe.Select(r => new RecipeItemDto(r.ProductId, r.Quantity)));

    private static IResult NameTaken(string what) =>
        Results.Problem($"{what} с таким названием уже есть.", statusCode: StatusCodes.Status409Conflict);
}
