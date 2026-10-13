using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ostatok.Api.Features;

/// <summary>
/// Обработчик исключений очищает заголовки ответа — вместе с разрешением для приложения, которое ходит с другого адреса.
/// Без него приложение вместо кода ошибки увидело бы «нет сети». Этот обработчик стоит первым, возвращает заголовки
/// и передаёт исключение дальше.
/// </summary>
public sealed class CorsOnErrors(ICorsService cors, ICorsPolicyProvider policies) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (await policies.GetPolicyAsync(http, null) is { } policy)
            cors.ApplyResult(cors.EvaluatePolicy(http, policy), http.Response);
        return false;
    }
}

/// <summary>
/// Последний рубеж: если запись всё-таки упёрлась в ограничение базы (два одинаковых запроса разом,
/// значение не помещается в столбец), клиент получает понятный отказ, а не «сбой сервера».
/// </summary>
public sealed class DbErrors : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        // Запись, которую меняли, уже удалил параллельный запрос (двойной щелчок по «Удалить»).
        if (exception is DbUpdateConcurrencyException)
        {
            await Results.Problem("Запись уже изменена или удалена. Обновите страницу.", statusCode: StatusCodes.Status404NotFound).ExecuteAsync(http);
            return true;
        }

        var error = exception as PostgresException ?? (exception as DbUpdateException)?.InnerException as PostgresException;
        var (status, text) = error?.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => (StatusCodes.Status409Conflict, "Такая запись уже есть."),
            PostgresErrorCodes.ForeignKeyViolation => (StatusCodes.Status409Conflict, "Запись связана с другими данными, изменить или удалить её нельзя."),
            // Нулевой байт и другие символы, которые база не хранит в тексте.
            PostgresErrorCodes.CharacterNotInRepertoire => (StatusCodes.Status400BadRequest, "В тексте есть недопустимые символы."),
            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NumericValueOutOfRange => (StatusCodes.Status400BadRequest, "Значение вне допустимых границ."),
            // Две операции помешали друг другу: ничего не записано, запрос можно повторить.
            PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure => (StatusCodes.Status409Conflict, "Одновременно шла другая операция с теми же продуктами. Повторите действие."),
            _ => (0, ""),
        };
        // База недоступна (нет соединения, тайм-аут, перезапуск): это не сбой сервера, запрос можно повторить позже.
        if (status == 0 && (exception as NpgsqlException ?? exception.InnerException as NpgsqlException) is { IsTransient: true })
            (status, text) = (StatusCodes.Status503ServiceUnavailable, "База данных временно недоступна. Повторите через минуту.");
        if (status == 0) return false;

        await Results.Problem(text, statusCode: status).ExecuteAsync(http);
        return true;
    }
}
