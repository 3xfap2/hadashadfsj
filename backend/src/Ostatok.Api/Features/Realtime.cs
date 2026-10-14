using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ostatok.Api.Features;

/// <summary>
/// Рассылка «данные изменились» всем открытым кабинетам. Сообщение не несёт самих данных:
/// экран, получив его, перечитывает то, что показывает, — так он всегда совпадает с сервером.
/// Тот же сигнал сбрасывает кэш чтений сервера (<see cref="ReadCache"/>): перечитанное придёт уже из базы.
/// </summary>
public sealed class EventBus(ReadCache cache)
{
    private readonly ConcurrentDictionary<Guid, (Guid Restaurant, Channel<string> Channel)> _subscribers = new();

    /// <summary>Сообщает кабинетам ресторана об изменении; без ресторана (фоновая задача по всем сразу) — всем.</summary>
    public void Publish(Guid? restaurant = null)
    {
        // Сначала кэш: экран, получив сообщение, не должен прочитать прежний ответ.
        cache.Invalidate(restaurant);
        foreach (var (owner, channel) in _subscribers.Values)
            if (restaurant is null || owner == restaurant) channel.Writer.TryWrite("changed");
    }

    /// <summary>Подписка одного кабинета. Пока она не закрыта, в её канал приходят сообщения об изменениях.</summary>
    public Subscription Subscribe(Guid restaurant)
    {
        var id = Guid.NewGuid();
        // Если клиент не успевает читать, старые сообщения отбрасываются: ему достаточно одного «изменилось».
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = (restaurant, channel);
        return new Subscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    public sealed class Subscription(ChannelReader<string> reader, Action close) : IDisposable
    {
        public ChannelReader<string> Reader => reader;
        public void Dispose() => close();
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
                using var events = bus.Subscribe(tenant.Id);
                while (!ct.IsCancellationRequested)
                {
                    // Раз в 25 секунд — пустая строка-«пульс», иначе прокси закрывает молчащее соединение.
                    using var pulse = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pulse.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        await http.Response.WriteAsync($"data: {await events.Reader.ReadAsync(pulse.Token)}\n\n", ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
