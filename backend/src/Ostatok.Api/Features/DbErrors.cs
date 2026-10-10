using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ostatok.Api.Features;

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
