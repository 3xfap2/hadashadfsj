using System.Collections.Concurrent;

namespace Ostatok.Api.Features;

/// <summary>Счётчики кэша для /health: сколько ответов отдано из памяти и сколько посчитано по базе.</summary>
public sealed record CacheStats(long Hits, long Misses, long Invalidations, int Entries);

/// <summary>
/// Кэш самых частых чтений — стоп-листа, горячих предложений и остатков — в памяти процесса.
/// Их запрашивает каждый терминал официанта и каждый открытый экран, а меняются они только при записи в базу.
///
/// Ключ: ресторан + версия данных ресторана + что ещё меняет ответ (адрес, параметры, видны ли роли закупочные цены).
/// Версия растёт после каждого зафиксированного изменения данных ресторана (<see cref="EventBus.Publish"/>
/// вызывается из перехвата коммита — <see cref="PublishChanges"/>), и записи ресторана тут же удаляются.
/// Версия читается до похода в базу: если запись зафиксировали, пока ответ считался, он ляжет под старой версией
/// и его никто не прочитает — кэш не отстаёт от базы даже при гонке.
///
/// Ответ зависит и от времени: партия истекает без записи в базу, часы до конца срока и прогноз стопа идут сами.
/// Поэтому запись живёт до ближайшего из двух моментов: срок ближайшей годной партии (в этот миг меняются статусы
/// стоп-листа — значит, после него ответ считается заново) и <see cref="Ttl"/> — за 15 секунд «осталось 3,4 ч»
/// и прогноз стопа сдвигаются незаметно, а в базу идёт не больше одного расчёта в 15 секунд на ресторан и роль.
///
/// Кэш не участвует в решении о продаже: заказ (<see cref="OrderService"/>) и списание читают партии из базы
/// под FOR UPDATE. Кэш только показывает — продать то, чего нет, из-за него нельзя.
///
/// Хранится в памяти процесса, а не в Redis: сервер один, общий кэш делить не с кем, а сеть до Redis дороже,
/// чем пересчёт. После перезапуска кэш пуст и заполняется первым же запросом: стоп-лист — это четыре запроса
/// с группировкой по индексу ресторана, десятки миллисекунд. При нескольких экземплярах сервера — Redis
/// и рассылка сброса через pub/sub или LISTEN/NOTIFY PostgreSQL.
/// </summary>
public sealed class ReadCache(TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public bool Enabled { get; init; } = true;

    private sealed record Entry(object Value, DateTimeOffset ValidUntil, string ETag);

    private readonly ConcurrentDictionary<(Guid Restaurant, string Key), Lazy<Task<Entry>>> _entries = new();
    private readonly ConcurrentDictionary<Guid, long> _versions = new();
    // Изменение без ресторана (фоновое автосписание по всем сразу) сбрасывает кэш всех ресторанов.
    private long _global;
    private long _hits, _misses, _invalidations, _etag;

    /// <summary>Данные ресторана изменились (null — неизвестно какого, значит всех): его записи больше не годятся.</summary>
    public void Invalidate(Guid? restaurant)
    {
        Interlocked.Increment(ref _invalidations);
        if (restaurant is { } id) _versions.AddOrUpdate(id, 1, (_, v) => v + 1);
        else Interlocked.Increment(ref _global);
        foreach (var key in _entries.Keys)
            if (restaurant is null || key.Restaurant == restaurant) _entries.TryRemove(key, out _);
    }

    public CacheStats Stats() => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _invalidations), _entries.Count);

    /// <summary>
    /// Ответ из кэша или посчитанный заново. load возвращает данные и момент, когда они изменятся сами по себе (срок ближайшей партии).
    /// Одинаковые запросы, пришедшие одновременно после сброса, ждут один расчёт, а не идут в базу каждый.
    /// Отдаёт ETag; запрос с тем же If-None-Match получает 304 без тела.
    /// </summary>
    public async Task<IResult> GetAsync<T>(HttpContext http, Guid restaurant, string key, Func<Task<(T Value, DateTimeOffset? ChangesAt)>> load)
        where T : class
    {
        // Кэш выключен (Cache:Enabled=false) — только для замера нагрузки «без кэша»: каждый запрос считается по базе.
        if (!Enabled)
        {
            Interlocked.Increment(ref _misses);
            http.Response.Headers["X-Cache"] = "off";
            return Results.Ok((await load()).Value);
        }

        var id = (restaurant, $"{key}|v{_versions.GetValueOrDefault(restaurant)}|g{Interlocked.Read(ref _global)}");
        Entry entry;
        while (true)
        {
            var mine = new Lazy<Task<Entry>>(() => LoadAsync(load));
            var found = _entries.GetOrAdd(id, mine);
            if (!ReferenceEquals(found, mine) && found.Value is { IsCompletedSuccessfully: true } done && done.Result.ValidUntil <= time.GetUtcNow())
            {
                // Запись устарела по времени: заменяется новой, если её не заменил соседний запрос.
                if (!_entries.TryUpdate(id, mine, found)) continue;
                found = mine;
            }

            if (ReferenceEquals(found, mine))
            {
                Interlocked.Increment(ref _misses);
                try
                {
                    // Расчёт идёт на соединении этого запроса и без его отмены: если он оборвётся, ждущие соседи всё равно получат ответ.
                    entry = await mine.Value;
                }
                catch
                {
                    _entries.TryRemove(new KeyValuePair<(Guid, string), Lazy<Task<Entry>>>(id, mine));
                    throw;
                }
                http.Response.Headers["X-Cache"] = "miss";
            }
            else
            {
                Interlocked.Increment(ref _hits);
                entry = await found.Value.WaitAsync(http.RequestAborted);
                http.Response.Headers["X-Cache"] = "hit";
            }
            break;
        }

        http.Response.Headers.ETag = entry.ETag;
        return http.Request.Headers.IfNoneMatch == entry.ETag ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok((T)entry.Value);
    }

    private async Task<Entry> LoadAsync<T>(Func<Task<(T Value, DateTimeOffset? ChangesAt)>> load)
    {
        var now = time.GetUtcNow();
        var (value, changesAt) = await load();
        var until = now + Ttl;
        if (changesAt is { } at && at < until) until = at;
        return new Entry(value!, until, $"W/\"{Interlocked.Increment(ref _etag)}\"");
    }
}
