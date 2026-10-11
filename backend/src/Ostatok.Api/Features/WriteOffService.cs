using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public enum WriteOffResult { Done, NotFound, Empty, ReasonRequired }

/// <summary>Почему списывают годную партию. Просроченная списывается как «Истёк срок годности» без выбора.</summary>
public enum WriteOffCause { Spoiled, Defective }

public sealed class WriteOffService(AppDbContext db, TimeProvider time)
{
    public const string System = "система";
    public const string DefectiveNote = "Брак поставки";
    public const string SpoiledNote = "Порча при хранении";

    public async Task<WriteOffResult> WriteOffAsync(Guid batchId, string actor, WriteOffCause? reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Та же блокировка, что и при заказе: списание не разойдётся с параллельной продажей.
        var batch = (await db.Batches
            .FromSql($"""SELECT * FROM "Batches" WHERE "Id" = {batchId} AND "RestaurantId" = {db.TenantId} FOR UPDATE""")
            .IgnoreQueryFilters()
            .ToListAsync(ct)).FirstOrDefault();
        if (batch is null) return WriteOffResult.NotFound;
        if (batch.Remaining == 0) return WriteOffResult.Empty;

        var expired = batch.ExpiresAt <= now;
        if (!expired && reason is null) return WriteOffResult.ReasonRequired;
        var note = expired ? "Истёк срок годности" : reason == WriteOffCause.Defective ? DefectiveNote : SpoiledNote;
        Apply(batch, now, note, actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return WriteOffResult.Done;
    }

    /// <summary>
    /// Списывает партии, просроченные дольше отсрочки. Отсрочка не длиннее срока хранения самой партии:
    /// продукт, который хранится час, не лежит просроченным полсуток. Возвращает число списанных партий.
    /// Работает по всем ресторанам сразу: это фоновая задача, у неё нет «своего» ресторана.
    /// </summary>
    public async Task<int> WriteOffExpiredAsync(TimeSpan grace, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batches = await db.Batches
            .FromSql($"""
                SELECT b.* FROM "Batches" b
                JOIN "Supplies" s ON s."Id" = b."SupplyId"
                WHERE b."Remaining" > 0 AND b."ExpiresAt" <= {now} - LEAST({grace}, b."ExpiresAt" - s."CreatedAt")
                FOR UPDATE OF b SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        foreach (var batch in batches) Apply(batch, now, "Истёк срок годности (автосписание)", System);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batches.Count;
    }

    private void Apply(Batch batch, DateTimeOffset now, string note, string actor)
    {
        db.Movements.Add(new Movement
        {
            RestaurantId = batch.RestaurantId,
            At = now,
            Type = MovementType.WriteOff,
            ProductId = batch.ProductId,
            BatchId = batch.Id,
            Quantity = batch.Remaining,
            Note = note,
            Actor = actor,
        });
        batch.Remaining = 0;
    }
}

/// <summary>Раз в 20 секунд поднимает сигналы о сроках, записывает историю стоп-листа и списывает просрочку, которую не списали вручную за время отсрочки.</summary>
public sealed class ExpiryWorker(IServiceScopeFactory scopes, StockOptions options, ILogger<ExpiryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.AutoWriteOff && !options.TrackStops) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (options.AutoWriteOff)
                {
                    var count = await scope.ServiceProvider.GetRequiredService<WriteOffService>()
                        .WriteOffExpiredAsync(TimeSpan.FromHours(options.AutoWriteOffAfterHours), stop);
                    if (count > 0) log.LogInformation("Автосписание: {Count} партий", count);
                }
                if (options.TrackStops)
                {
                    // Сигналы и история стоп-листа считаются отдельно для каждого ресторана.
                    var restaurants = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Restaurants.Select(r => r.Id).ToListAsync(stop);
                    foreach (var id in restaurants)
                    {
                        try
                        {
                            await using var own = scopes.CreateAsyncScope();
                            own.ServiceProvider.GetRequiredService<Tenant>().Id = id;
                            await own.ServiceProvider.GetRequiredService<AlertService>().RaiseBatchAlertsAsync(stop);
                            await own.ServiceProvider.GetRequiredService<StopTracker>().TrackAsync(stop);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            // Сбой в данных одного ресторана не должен оставить без сигналов остальных.
                            log.LogError(e, "Сигналы и стоп-лист ресторана {Restaurant} не обновлены", id);
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Фоновая задача не выполнена");
            }
        }
        while (await timer.WaitForNextTickAsync(stop));
    }
}
