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
        var error = exception as PostgresException ?? (exception as DbUpdateException)?.InnerException as PostgresException;
        var (status, text) = error?.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => (StatusCodes.Status409Conflict, "Такая запись уже есть."),
            PostgresErrorCodes.ForeignKeyViolation => (StatusCodes.Status409Conflict, "Запись связана с другими данными, изменить или удалить её нельзя."),
            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NumericValueOutOfRange => (StatusCodes.Status400BadRequest, "Значение вне допустимых границ."),
            _ => (0, ""),
        };
        if (status == 0) return false;

        await Results.Problem(text, statusCode: status).ExecuteAsync(http);
        return true;
    }
}
