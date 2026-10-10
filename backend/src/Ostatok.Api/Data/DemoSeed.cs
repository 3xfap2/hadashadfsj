using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

/// <summary>Демо-ресторан: наполняет пустую базу так, чтобы сразу были видны стоп, истекающие партии и просрочка.</summary>
public static class DemoSeed
{
    public static async Task RunAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        if (await db.Products.AnyAsync(ct)) return;

        Product P(string name, Unit unit, int shelfLifeHours, decimal cost) =>
            new() { Id = Guid.NewGuid(), Name = name, Unit = unit, ShelfLifeHours = shelfLifeHours, Cost = cost };

        var salmon = P("Лосось охлаждённый", Unit.Kg, 48, 1900);
        var shrimp = P("Креветки тигровые", Unit.Kg, 48, 1600);
        var beef = P("Говяжья вырезка", Unit.Kg, 72, 2400);
        var chicken = P("Куриное филе", Unit.Kg, 72, 420);
        var cream = P("Сливки 33%", Unit.L, 120, 480);
        var mozzarella = P("Моцарелла", Unit.Kg, 96, 850);
        var tomato = P("Томаты", Unit.Kg, 120, 280);
        var romaine = P("Салат романо", Unit.Kg, 72, 550);
        var avocado = P("Авокадо", Unit.Pcs, 96, 95);
        var parmesan = P("Пармезан", Unit.Kg, 720, 1700);
        var egg = P("Яйцо куриное", Unit.Pcs, 480, 14);
        var pasta = P("Паста феттучине", Unit.Kg, 4320, 300);
        var potato = P("Картофель", Unit.Kg, 720, 60);
        var tomYum = P("Основа том ям", Unit.L, 48, 350);
        var dough = P("Тесто для пиццы", Unit.Kg, 24, 90);
        var caesar = P("Соус цезарь", Unit.L, 72, 400);
        // Заготовки кухня делает сама: состав на литр.
        caesar.PrepRecipe = [new() { ProductId = parmesan.Id, Quantity = 0.15m }, new() { ProductId = egg.Id, Quantity = 4 }, new() { ProductId = cream.Id, Quantity = 0.2m }];
        tomYum.PrepRecipe = [new() { ProductId = shrimp.Id, Quantity = 0.1m }, new() { ProductId = tomato.Id, Quantity = 0.3m }, new() { ProductId = cream.Id, Quantity = 0.2m }];
        db.Products.AddRange(salmon, shrimp, beef, chicken, cream, mozzarella, tomato, romaine, avocado, parmesan, egg, pasta, potato, tomYum, dough, caesar);

        Dish D(string name, decimal price, string photo, string category, params (Product Product, decimal Quantity)[] recipe) => new()
        {
            Category = category,
            Id = Guid.NewGuid(),
            Name = name,
            Price = price,
            PhotoUrl = $"/dishes/{photo}.jpg",
            Recipe = recipe.Select(r => new RecipeItem { ProductId = r.Product.Id, Quantity = r.Quantity }).ToList(),
        };

        db.Dishes.AddRange(
            D("Том ям с креветками", 690, "tom-yum", "Супы", (tomYum, 0.3m), (shrimp, 0.09m), (cream, 0.05m), (tomato, 0.04m)),
            D("Стейк из лосося", 1190, "salmon-steak", "Горячее", (salmon, 0.22m), (potato, 0.2m), (cream, 0.03m)),
            D("Феттучине с лососем", 840, "fettuccine", "Паста", (pasta, 0.12m), (salmon, 0.11m), (cream, 0.1m), (parmesan, 0.015m)),
            D("Боул с лососем и авокадо", 790, "bowl", "Горячее и боулы", (salmon, 0.1m), (avocado, 1), (romaine, 0.06m)),
            D("Цезарь с курицей", 590, "caesar-chicken", "Салаты", (romaine, 0.12m), (chicken, 0.11m), (caesar, 0.04m), (parmesan, 0.015m), (egg, 1)),
            D("Цезарь с креветками", 720, "caesar-shrimp", "Салаты", (romaine, 0.12m), (shrimp, 0.1m), (caesar, 0.04m), (parmesan, 0.015m), (egg, 1)),
            D("Пицца «Маргарита»", 620, "margherita", "Пицца", (dough, 0.28m), (mozzarella, 0.15m), (tomato, 0.12m)),
            D("Филе миньон", 1890, "filet-mignon", "Горячее", (beef, 0.22m), (potato, 0.2m)),
            D("Тартар из говядины", 890, "tartare", "Закуски", (beef, 0.13m), (egg, 1), (parmesan, 0.01m)));

        var zone = TimeSpan.FromHours(3);
        var morning = new DateTimeOffset(now.ToOffset(zone).Date, zone).AddHours(8).ToUniversalTime();
        if (morning > now.AddMinutes(-30)) morning = now.AddMinutes(-30);

        // Поставки заданы через «сколько часов осталось до конца срока»: отрицательное значение — уже просрочено.
        Supply S(string number, string supplier, params (Product Product, decimal Quantity, double HoursLeft)[] items)
        {
            // Текущие поставки приняты сегодня утром, уже после истории: так они не «лежали на складе», пока история
            // расходовала другие партии. Срок у партии свой — остаточный, как у товара с коротким сроком от поставщика.
            var supply = new Supply { Id = Guid.NewGuid(), Number = number, Supplier = supplier, CreatedAt = morning };
            foreach (var (product, quantity, hoursLeft) in items)
            {
                var expiresAt = now.AddHours(hoursLeft);
                // Просроченная партия должна была пролежать хотя бы полсуток, иначе её сразу спишет автосписание.
                var createdAt = expiresAt.AddHours(-13);
                if (createdAt < supply.CreatedAt) supply.CreatedAt = createdAt;
                supply.Batches.Add(new Batch { Id = Guid.NewGuid(), ProductId = product.Id, Quantity = quantity, Remaining = quantity, ExpiresAt = expiresAt });
            }
            return supply;
        }

        var supplies = new[]
        {
            S("П-0409", "Молочный двор", (cream, 0.9m, -3), (mozzarella, 1.1m, -1.5), (parmesan, 1.4m, 500), (pasta, 5, 3000), (potato, 18, 400), (egg, 60, 300)),
            S("П-0410", "«Северная рыба»", (salmon, 0.7m, 5), (shrimp, 1.2m, 7), (tomYum, 2.4m, 6), (romaine, 0.9m, 9), (dough, 1.4m, 10), (chicken, 1.8m, 20)),
            S("П-0411", "«Северная рыба»", (caesar, 0.6m, 30), (shrimp, 2, 40), (romaine, 1.5m, 50), (beef, 2.6m, 52)),
            S("П-0412", "Овощная база №3", (chicken, 3, 60), (tomato, 5.2m, 70), (cream, 4, 90)),
        };
        db.Supplies.AddRange(supplies);
        db.Movements.AddRange(supplies.SelectMany(s => s.Batches.Select(b => new Movement
        {
            At = s.CreatedAt,
            Type = MovementType.Supply,
            ProductId = b.ProductId,
            BatchId = b.Id,
            Quantity = b.Quantity,
            Note = $"Поставка {s.Number}",
        })));

        DemoHistory.Add(db, db.Products.Local.ToList(), db.Dishes.Local.ToList(), now);

        // Заказы истории нумеруются по времени, счётчик ресторана продолжает с последнего номера.
        var number = 0;
        foreach (var order in db.Orders.Local.OrderBy(o => o.CreatedAt)) order.Number = ++number;
        (await db.Restaurants.FirstAsync(r => r.Id == db.TenantId, ct)).LastOrderNumber = number;

        await db.SaveChangesAsync(ct);
    }
}
