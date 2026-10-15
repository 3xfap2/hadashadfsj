using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record IntegrationProduct(string Name, Unit Unit);

/// <param name="Available">Можно заказать прямо сейчас: продуктов хватает хотя бы на порцию и блюдо не снято с продажи.</param>
/// <param name="Portions">Сколько порций можно приготовить из годных остатков; пусто, если у блюда нет состава.</param>
/// <param name="Reason">Почему блюдо в стоп-листе: OutOfStock, NotEnough, Expired, Manual; пусто, если доступно.</param>
public sealed record IntegrationDish(Guid DishId, string Name, string? Category, decimal Price, bool Available, int? Portions, StopReason? Reason);
public sealed record IntegrationMenu(string Restaurant, IEnumerable<IntegrationDish> Dishes);

/// <summary>
/// Справочники ресторана для внешних систем по ключу (X-Api-Key): поставщику — названия продуктов, кассе и сайту — меню со стоп-листом.
/// Закупочные цены, остатки и сроки партий наружу не отдаются.
/// </summary>
public static class IntegrationEndpoints
{
    public static void MapIntegration(this RouteGroupBuilder api)
    {
        var integration = api.MapGroup("/integration").WithTags("Интеграции");

        integration.MapGet("/products", async (AppDbContext db, CancellationToken ct) =>
            await db.Products.Where(p => p.PrepRecipe.Count == 0).OrderBy(p => p.Name).Select(p => new IntegrationProduct(p.Name, p.Unit)).ToListAsync(ct))
            .RequireKey(null)
            .Produces<List<IntegrationProduct>>()
            .WithSummary("Справочник продуктов ресторана для поставщика")
            .WithDescription("Названия и единицы покупных продуктов (заготовки ресторан делает сам и в списке их нет). Цен и остатков нет. Ключ с любым правом. Название из этого списка подходит в productName входящей поставки.");

        integration.MapGet("/menu", async (AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var dishes = await db.LoadDishesAsync(ct);
            var totals = await db.LoadTotalsAsync(time.GetUtcNow(), ct);
            var restaurant = await db.CurrentRestaurantAsync(ct);
            return new IntegrationMenu(restaurant.Name, dishes
                .OrderBy(d => d.Category).ThenBy(d => d.Name)
                .Select(d =>
                {
                    var a = Availability.Of(d, totals);
                    return new IntegrationDish(d.Id, d.Name, d.Category, d.Price, a.Status != DishStatus.Stop,
                        a.Portions == int.MaxValue ? null : a.Portions, a.Reason);
                }));
        })
            .RequireKey(IntegrationKey.Orders)
            .Produces<IntegrationMenu>()
            .WithSummary("Меню ресторана со стоп-листом для кассы и сайта")
            .WithDescription("Все блюда с ценами; у блюд в стоп-листе Available = false и причина. Нужен ключ с правом orders. Заказ оформляется методом POST /api/orders/external; доступность там проверяется заново под блокировкой партий.");
    }
}
