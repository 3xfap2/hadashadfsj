using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record SimulatorSettings(bool Running, int OrdersPerMinute);

/// <summary>Состояние генератора. Хранится в памяти: после перезапуска сервера генератор выключен.</summary>
public sealed class SimulatorState
{
    private static readonly SimulatorSettings Off = new(false, 6);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SimulatorSettings> _settings = new();

    /// <summary>Настройки генератора ресторана; у каждого ресторана он свой.</summary>
    public SimulatorSettings Get(Guid restaurant) => _settings.GetValueOrDefault(restaurant, Off);

    public void Set(Guid restaurant, SimulatorSettings settings) => _settings[restaurant] = settings;

    public IReadOnlyList<(Guid Restaurant, SimulatorSettings Settings)> Running() =>
        _settings.Where(s => s.Value.Running).Select(s => (s.Key, s.Value)).ToList();
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
    private readonly Dictionary<Guid, double> _debt = [];   // дробная часть заказов, накопленная между тактами
    private int _ticks;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stop))
        {
            _ticks++;
            foreach (var (restaurant, settings) in state.Running())
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<Tenant>().Id = restaurant;
                    var debt = _debt.GetValueOrDefault(restaurant) + settings.OrdersPerMinute * Tick.TotalMinutes;
                    for (; debt >= 1; debt--) await PlaceOrderAsync(scope.ServiceProvider, stop);
                    _debt[restaurant] = debt;
                    // Раз в минуту «приезжает поставщик» и довозит то, что заканчивается.
                    if (_ticks % 12 == 0) await RestockAsync(scope.ServiceProvider, stop);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Генератор событий: такт не выполнен");
                }
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

public sealed record ExpiryCheckRequest(Guid ProductId, int Minutes);

/// <param name="WrittenOffAt">Когда партию списали; пусто, пока она на складе.</param>
public sealed record ExpiryCheckResponse(
    Guid BatchId, string SupplyNumber, Guid ProductId, string ProductName, Unit Unit,
    decimal Quantity, decimal Remaining, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? WrittenOffAt);

public static class SimulatorEndpoints
{
    public const string CheckSupplier = "Проверка сроков";

    public static void MapSimulator(this RouteGroupBuilder api)
    {
        var simulator = api.MapGroup("/simulator").WithTags("Генератор событий").RequireRoles(Access.Catalog);

        simulator.MapGet("", (SimulatorState state, Tenant tenant) => state.Get(tenant.Id));

        simulator.MapPut("", (SimulatorSettings req, SimulatorState state, Tenant tenant, EventBus bus) =>
        {
            if (req.OrdersPerMinute is < 1 or > 120)
                return new Errors().Add("ordersPerMinute", "Заказов в минуту — от 1 до 120.").ToResult();
            state.Set(tenant.Id, req);
            bus.Publish();
            return Results.Ok(req);
        });

        // Проверка сроков: партия, которая истекает через несколько минут. На ней виден весь путь —
        // сигнал, просрочка, стоп блюда, автосписание — без ожидания в сутки.
        simulator.MapPost("/expiry-check", async (ExpiryCheckRequest req, AppDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var product = await db.Products.FindAsync([req.ProductId], ct);
            var errors = new Errors()
                .Check(product is not null, "productId", "Такого продукта нет.")
                .Check(req.Minutes is >= 1 and <= 30, "minutes", "Срок — от 1 до 30 минут.");
            if (errors.Any) return errors.ToResult();

            var now = time.GetUtcNow();
            // Количества хватает на пять порций самого «прожорливого» блюда с этим продуктом.
            var portion = await db.Dishes.SelectMany(d => d.Recipe).Where(r => r.ProductId == req.ProductId).MaxAsync(r => (decimal?)r.Quantity, ct) ?? 0.2m;
            var number = $"Т-{await db.Supplies.CountAsync(s => s.Supplier == CheckSupplier, ct) + 1:0000}";
            var supply = new Supply { Id = Guid.NewGuid(), Number = number, Supplier = CheckSupplier, CreatedAt = now };
            var batch = new Batch
            {
                Id = Guid.NewGuid(), SupplyId = supply.Id, ProductId = product!.Id,
                Quantity = portion * 5, Remaining = portion * 5, ExpiresAt = now.AddMinutes(req.Minutes),
            };
            supply.Batches.Add(batch);
            db.Supplies.Add(supply);
            db.Movements.Add(new Movement
            {
                At = now, Type = MovementType.Supply, ProductId = product.Id, BatchId = batch.Id,
                Quantity = batch.Quantity, Note = $"Поставка {number}", Actor = user.Identity?.Name ?? "",
            });
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/simulator/expiry-check", await LastCheckAsync(db, ct));
        });

        simulator.MapGet("/expiry-check", async (AppDbContext db, CancellationToken ct) =>
            Results.Json(await LastCheckAsync(db, ct)));
    }

    private static async Task<ExpiryCheckResponse?> LastCheckAsync(AppDbContext db, CancellationToken ct)
    {
        var batch = await db.Batches.AsNoTracking()
            .Include(b => b.Product).Include(b => b.Supply)
            .Where(b => b.Supply!.Supplier == CheckSupplier)
            .OrderByDescending(b => b.Supply!.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (batch is null) return null;

        var writtenOff = await db.Movements
            .Where(m => m.BatchId == batch.Id && m.Type == MovementType.WriteOff)
            .Select(m => (DateTimeOffset?)m.At).FirstOrDefaultAsync(ct);
        return new ExpiryCheckResponse(
            batch.Id, batch.Supply!.Number, batch.ProductId, batch.Product!.Name, batch.Product.Unit,
            batch.Quantity, batch.Remaining, batch.Supply.CreatedAt, batch.ExpiresAt, writtenOff);
    }
}
