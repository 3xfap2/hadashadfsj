using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record SimulatorSettings(bool Running, int OrdersPerMinute);

/// <summary>Состояние генератора. Хранится в памяти: после перезапуска сервера генератор выключен.</summary>
public sealed class SimulatorState
{
    private readonly Lock _gate = new();
    private SimulatorSettings _settings = new(false, 6);

    public SimulatorSettings Settings
    {
        get { lock (_gate) return _settings; }
        set { lock (_gate) _settings = value; }
    }
}

/// <summary>
/// Генератор событий: имитирует кассу и поставщиков. Заказы идут теми же путями, что и настоящие
/// (OrderService), поэтому на нём видно, как система ведёт себя под потоком заказов.
/// </summary>
public sealed class Simulator(IServiceScopeFactory scopes, SimulatorState state, TimeProvider time, ILogger<Simulator> log) : BackgroundService
{
    public const string Actor = "генератор";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private readonly Random _random = new();
    private double _debt;        // дробная часть заказов, накопленная между тактами
    private int _ticks;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stop))
        {
            var settings = state.Settings;
            if (!settings.Running) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                _debt += settings.OrdersPerMinute * Tick.TotalMinutes;
                for (; _debt >= 1; _debt--) await PlaceOrderAsync(scope.ServiceProvider, stop);
                // Раз в минуту «приезжает поставщик» и довозит то, что заканчивается.
                if (++_ticks % 12 == 0) await RestockAsync(scope.ServiceProvider, stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Генератор событий: такт не выполнен");
            }
        }
    }

    private async Task PlaceOrderAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var dishes = await db.Dishes.Where(d => !d.ManuallyStopped).Select(d => d.Id).ToListAsync(ct);
        if (dishes.Count == 0) return;
        var places = services.GetRequiredService<RestaurantOptions>().Places;

        // Блюда выбираются вслепую, как их заказывают гости: иногда заказ упирается в нехватку и отклоняется.
        var lines = Enumerable.Range(0, _random.Next(1, 3))
            .Select(_ => dishes[_random.Next(dishes.Count)])
            .GroupBy(id => id)
            .Select(g => new OrderLine(g.Key, g.Count()))
            .ToList();
        var place = places.Length > 0 ? places[_random.Next(places.Length)] : "";
        await services.GetRequiredService<OrderService>().PlaceAsync(lines, place, Actor, ct);
    }

    private async Task RestockAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var options = services.GetRequiredService<StockOptions>();
        var now = time.GetUtcNow();
        var products = await db.Products.ToListAsync(ct);
        var needed = Forecast.Purchases(
                products,
                await db.LoadDishesAsync(ct),
                (await db.LoadFreshBatchesAsync(now, ct)).ToLookup(b => b.ProductId),
                await db.LoadConsumptionPerHourAsync(now, options, ct),
                now, options.PurchaseCoverHours)
            .Where(s => (s.HoursCovered ?? 0) < 12)
            .Take(4)
            .ToList();
        if (needed.Count == 0) return;

        var supply = new Supply { Id = Guid.NewGuid(), Number = $"Г-{now:MMdd-HHmmss}", Supplier = "Генератор событий", CreatedAt = now };
        foreach (var item in needed)
        {
            var product = products.First(p => p.Id == item.ProductId);
            var batch = new Batch
            {
                Id = Guid.NewGuid(), ProductId = product.Id, Quantity = item.Quantity, Remaining = item.Quantity,
                ExpiresAt = now.AddHours(product.ShelfLifeHours),
            };
            supply.Batches.Add(batch);
            db.Movements.Add(new Movement
            {
                At = now, Type = MovementType.Supply, ProductId = product.Id, BatchId = batch.Id,
                Quantity = batch.Quantity, Note = $"Поставка {supply.Number}", Actor = Actor,
            });
        }
        db.Supplies.Add(supply);
        await db.SaveChangesAsync(ct);
    }
}

public static class SimulatorEndpoints
{
    public static void MapSimulator(this RouteGroupBuilder api)
    {
        var simulator = api.MapGroup("/simulator").WithTags("Генератор событий").RequireRoles(Access.Catalog);

        simulator.MapGet("", (SimulatorState state) => state.Settings);

        simulator.MapPut("", (SimulatorSettings req, SimulatorState state, EventBus bus) =>
        {
            if (req.OrdersPerMinute is < 1 or > 120)
                return new Errors().Add("ordersPerMinute", "Заказов в минуту — от 1 до 120.").ToResult();
            state.Settings = req;
            bus.Publish();
            return Results.Ok(state.Settings);
        });
    }
}
