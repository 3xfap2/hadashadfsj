using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ostatok.Api.Features;

/// <summary>
/// Рассылка «данные изменились» всем открытым кабинетам. Сообщение не несёт самих данных:
/// экран, получив его, перечитывает то, что показывает, — так он всегда совпадает с сервером.
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Guid, (Guid Restaurant, Channel<string> Channel)> _subscribers = new();

    /// <summary>Сообщает кабинетам ресторана об изменении; без ресторана (фоновая задача по всем сразу) — всем.</summary>
    public void Publish(Guid? restaurant = null)
    {
        foreach (var (owner, channel) in _subscribers.Values)
            if (restaurant is null || owner == restaurant) channel.Writer.TryWrite("changed");
    }

    public async IAsyncEnumerable<string> Subscribe(Guid restaurant, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        // Если клиент не успевает читать, старые сообщения отбрасываются: ему достаточно одного «изменилось».
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = (restaurant, channel);
        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct)) yield return message;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Сообщает об изменении после каждой успешной записи в базу. Внутри транзакции ждёт её завершения:
/// иначе экран перечитал бы данные раньше, чем они стали видны другим соединениям.
/// </summary>
public sealed class PublishChanges(EventBus bus) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken ct = default)
    {
        if (result > 0 && e.Context?.Database.CurrentTransaction is null) bus.Publish(Owner(e.Context));
        return ValueTask.FromResult(result);
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData e, CancellationToken ct = default)
    {
        bus.Publish(Owner(e.Context));
        return Task.CompletedTask;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData e) => bus.Publish(Owner(e.Context));

    private static Guid? Owner(DbContext? context) =>
        context is Data.AppDbContext { TenantId: var id } && id != Guid.Empty ? id : null;
}

public static class RealtimeEndpoints
{
    public const string Path = "/api/events";

    public static void MapRealtime(this IEndpointRouteBuilder app)
    {
        // Поток событий (SSE). Браузер держит соединение открытым и сам переподключается при обрыве.
        app.MapGet(Path, async (HttpContext http, EventBus bus, Data.Tenant tenant, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";   // чтобы прокси не копил сообщения
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            try
            {
                await using var events = bus.Subscribe(tenant.Id, ct).GetAsyncEnumerator(ct);
                var next = events.MoveNextAsync().AsTask();
                while (!ct.IsCancellationRequested)
                {
                    // Раз в 25 секунд — пустая строка-«пульс», иначе прокси закрывает молчащее соединение.
                    var pulse = Task.Delay(TimeSpan.FromSeconds(25), ct);
                    if (await Task.WhenAny(next, pulse) == next)
                    {
                        if (!await next) break;
                        await http.Response.WriteAsync($"data: {events.Current}\n\n", ct);
                        next = events.MoveNextAsync().AsTask();
                    }
                    else
                    {
                        await http.Response.WriteAsync(": ping\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Клиент закрыл страницу — обычное завершение потока.
            }
        })
        .WithTags("События")
        .RequireAuthorization();
    }
}
