using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <param name="Intact">Цепочка цела: каждая запечатанная запись совпадает со своим хешем.</param>
/// <param name="Sealed">Сколько движений запечатано.</param>
/// <param name="Unsealed">Сколько новых движений ещё ждут печати (печать идёт раз в 30 секунд).</param>
/// <param name="BrokenMovementId">Первое движение, на котором цепочка разошлась; пусто, если цела.</param>
/// <param name="BrokenSealNo">Номер этой записи в цепочке.</param>
public sealed record JournalVerifyResult(
    bool Intact, int Sealed, int Unsealed, string? LastHash, DateTimeOffset? LastSealedAt,
    long? BrokenMovementId, long? BrokenSealNo, string Message);

/// <summary>
/// Печать журнала движений: хэш-цепочка, по которой видна незаметная правка или удаление записи прямо в базе.
/// Каждое движение получает номер в цепочке ресторана и Hash = SHA-256(Hash предыдущей записи + неизменяемые поля движения).
/// Правка любого поля, удаление или вставка записи в середину ломают цепочку с этого места.
///
/// Печать идёт отдельно от записи движений: заказы, поставки и списания пишут журнал как прежде, а фоновая задача
/// раз в 30 секунд запечатывает новые записи. Порядок в цепочке — порядок печати (номер SealNo), а не Id: транзакция,
/// начатая раньше, может закончиться позже и получить меньший Id уже после печати соседей.
///
/// Чего печать не защищает: администратор базы может переписать и записи, и все хеши после них. Против этого
/// последний хеш нужно регулярно выгружать во внешнее хранилище (письмо, другой сервер) и сверять — это следующий шаг.
/// </summary>
public static class JournalSeal
{
    /// <summary>Сколько движений запечатывается за один проход по ресторану.</summary>
    public const int Batch = 2000;

    /// <summary>Запечатывает ещё не запечатанные движения текущего ресторана по возрастанию Id. Возвращает, сколько запечатано.</summary>
    public static async Task<int> SealAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var last = await db.Movements.AsNoTracking().Where(m => m.SealNo != null).OrderByDescending(m => m.SealNo)
            .Select(m => new { m.SealNo, m.Hash }).FirstOrDefaultAsync(ct);
        var rows = await db.Movements.AsNoTracking().Where(m => m.SealNo == null).OrderBy(m => m.Id).Take(Batch).ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var (no, hash) = (last?.SealNo ?? 0, last?.Hash ?? "");
        var ids = new long[rows.Count];
        var nos = new long[rows.Count];
        var hashes = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            hash = HashOf(hash, rows[i]);
            (ids[i], nos[i], hashes[i]) = (rows[i].Id, ++no, hash);
        }
        // Одна команда на всю пачку: либо запечатана вся, либо ничего. Номер в цепочке уникален у ресторана (индекс),
        // поэтому второй печатник, начавший одновременно, упрётся в ошибку и продолжит со следующего прохода.
        // Пишется командой, а не SaveChanges: печать — служебная запись, кабинеты из-за неё перечитывать данные не должны.
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE "Movements" AS m SET "SealNo" = v.no, "Hash" = v.hash, "SealedAt" = {now}
            FROM unnest({ids}, {nos}, {hashes}) AS v(id, no, hash)
            WHERE m."Id" = v.id AND m."RestaurantId" = {db.TenantId} AND m."SealNo" IS NULL
            """, ct);
    }

    /// <summary>Пересчитывает цепочку текущего ресторана с начала и сравнивает с записанными хешами.</summary>
    public static async Task<JournalVerifyResult> VerifyAsync(AppDbContext db, CancellationToken ct)
    {
        var (count, hash, expectedNo) = (0, "", 1L);
        DateTimeOffset? sealedAt = null;
        Movement? broken = null;
        await foreach (var m in db.Movements.AsNoTracking().Where(m => m.SealNo != null).OrderBy(m => m.SealNo).AsAsyncEnumerable().WithCancellation(ct))
        {
            // Пропуск номера — запись удалили; несовпадение хеша — запись или хеш поменяли.
            if (m.SealNo != expectedNo || HashOf(hash, m) != m.Hash)
            {
                broken = m;
                break;
            }
            (hash, sealedAt, count, expectedNo) = (m.Hash!, m.SealedAt, count + 1, expectedNo + 1);
        }
        var unsealed = await db.Movements.CountAsync(m => m.SealNo == null, ct);
        if (broken is not null)
            return new JournalVerifyResult(false, count, unsealed, count > 0 ? hash : null, sealedAt, broken.Id, broken.SealNo,
                broken.SealNo != expectedNo
                    ? $"Цепочка нарушена: после записи № {expectedNo - 1} идёт № {broken.SealNo} (движение {broken.Id}) — запись удалена или вставлена в обход журнала."
                    : $"Цепочка нарушена на записи № {broken.SealNo} (движение {broken.Id}): запись изменена в базе после печати.");
        return new JournalVerifyResult(true, count, unsealed, count > 0 ? hash : null, sealedAt, null, null,
            count == 0 ? "Журнал ещё не запечатан." : $"Цепочка цела: запечатано записей — {count}.");
    }

    /// <summary>
    /// SHA-256 от предыдущего хеша и неизменяемых полей движения. Каждое поле — с длиной впереди: так содержимое
    /// нельзя перенести из одного поля в соседнее, не изменив хеш.
    /// </summary>
    public static string HashOf(string previous, Movement m)
    {
        var text = new StringBuilder();
        void Field(string? value) => text.Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
        Field(previous);
        Field(m.Id.ToString(CultureInfo.InvariantCulture));
        Field(m.RestaurantId.ToString("N"));
        Field(m.At.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Field(m.Type.ToString());
        Field(m.ProductId.ToString("N"));
        Field(m.BatchId.ToString("N"));
        // В базе три знака после запятой: 2 и 2.000 — одно и то же количество.
        Field(m.Quantity.ToString("0.000", CultureInfo.InvariantCulture));
        Field(m.OrderId?.ToString("N"));
        Field(m.Note);
        Field(m.Actor);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static void MapJournalSeal(this RouteGroupBuilder api) =>
        api.MapGet("/stock/journal/verify", (AppDbContext db, CancellationToken ct) => JournalSeal.VerifyAsync(db, ct))
            .RequireRoles(Access.Staff)
            .WithTags("Склад")
            .WithSummary("Проверить печать журнала движений")
            .WithDescription("Пересчитывает хэш-цепочку журнала ресторана с первой записи. Цела — число запечатанных записей и последний хеш; нарушена — номер первой записи, где хеш не сошёлся или пропущен номер. Защищает от незаметной правки журнала в базе; администратор базы, переписавший и хеши, обнаруживается только сверкой последнего хеша с выгруженным вовне.");
}

/// <summary>Раз в 30 секунд запечатывает новые движения каждого ресторана (см. <see cref="JournalSeal"/>). Journal:Seal = false выключает.</summary>
public sealed class JournalSealer(IServiceScopeFactory scopes, IConfiguration config, TimeProvider time, ILogger<JournalSealer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!config.GetValue("Journal:Seal", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var restaurants = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Restaurants.Select(r => r.Id).ToListAsync(stop);
                foreach (var id in restaurants)
                {
                    try
                    {
                        await using var own = scopes.CreateAsyncScope();
                        own.ServiceProvider.GetRequiredService<Tenant>().Id = id;
                        await JournalSeal.SealAsync(own.ServiceProvider.GetRequiredService<AppDbContext>(), time.GetUtcNow(), stop);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogError(e, "Журнал ресторана {Restaurant} не запечатан", id);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Печать журнала не выполнена");
            }
        }
        while (await timer.WaitForNextTickAsync(stop));
    }
}
