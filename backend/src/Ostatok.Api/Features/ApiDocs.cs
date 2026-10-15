using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace Ostatok.Api.Features;

/// <summary>
/// Описание API (/openapi/v1.json) и страница Swagger UI (/swagger) — открыты и на сервере, как сам документ.
/// Схемы доступа: токен входа (Bearer) и ключ интеграции ресторана (X-Api-Key) для поставок из внешних систем.
/// Описания ключевых методов задаются здесь по адресу, чтобы не трогать сами эндпоинты;
/// если у эндпоинта есть своё WithSummary — остаётся оно.
/// </summary>
public static class ApiDocs
{
    public const string Path = "/swagger";
    private const string Bearer = "Bearer";
    private const string ApiKey = "ApiKey";

    private static readonly Dictionary<string, (string Summary, string Description)> Operations = new()
    {
        ["POST api/auth/login"] = ("Вход: токен по логину и паролю",
            "Возвращает токен для заголовка Authorization: Bearer. Демо-записи: demo, manager, cook, storekeeper, waiter, пароль demo2026. "
            + "После пяти неверных паролей логин закрывается на 10 минут (429)."),
        ["POST api/orders"] = ("Оформить заказ (официант)",
            "Одна транзакция: годные партии нужных продуктов блокируются (SELECT … FOR UPDATE) и списываются начиная с ближайшего срока. "
            + "Не хватает хотя бы одного продукта — 409 со списком нехватки, ничего не списано. "
            + "Повтор с тем же id возвращает прежний заказ и ничего не списывает."),
        ["POST api/supplies"] = ("Поставка, внесённая сотрудником склада",
            "Каждая строка становится партией; срок годности — дата поставки + срок хранения строки или норматив продукта. Повтор номера — 409."),
        ["POST api/supplies/incoming"] = ("Поставка из внешней системы",
            "Учётная программа или сервис поставщика присылает поставку по ключу интеграции ресторана в заголовке X-Api-Key "
            + "(или сотрудник склада — по токену). Ответ 202: поставка ждёт подтверждения в кабинете, остатки не меняются. "
            + "Повтор id или номера — 409, неверный ключ — 401."),
        ["GET api/supplies/incoming"] = ("Поставки, ждущие подтверждения", "Поставки из внешних систем, которые ещё не приняты и не отклонены."),
        ["POST api/supplies/incoming/{id}/accept"] = ("Приёмка поставки",
            "Продукты встают на склад партиями. Можно принять частично: по строкам указывается принятое количество (0 — строка не принята). "
            + "Срок годности считается от даты создания поставки; если он истёк, пока поставка ждала, — 409."),
        ["POST api/supplies/incoming/{id}/reject"] = ("Отклонить поставку", "Поставка остаётся в истории, на склад ничего не добавляется."),
        ["GET api/stop-list"] = ("Стоп-лист",
            "Сколько порций каждого блюда можно приготовить из годных остатков, причина стопа и прогноз, когда блюдо уйдёт в стоп. "
            + "Ответ из кэша чтений (X-Cache: hit|miss, ETag/If-None-Match → 304); кэш сбрасывается при каждой записи в базу."),
        ["GET api/hot-offers"] = ("Горячие предложения",
            "Блюда, которые стоит продать первыми, чтобы не пропали истекающие продукты: приоритет, порции, сумма под риском, причина и скидка."),
        ["GET api/hot-offers/plan"] = ("План спасения", "Сколько рублей истекающих продуктов уйдёт в блюда и что пристроить некуда."),
        ["GET api/stock"] = ("Остатки по продуктам", "Годный и просроченный остаток, число партий и ближайший срок."),
        ["GET api/stock/batches"] = ("Партии на складе", "Партии с остатком: срок, часы до конца срока, статус (годна, истекает, просрочена)."),
        ["POST api/stock/batches/{id}/write-off"] = ("Списать партию",
            "Списывает весь остаток партии под той же блокировкой, что и заказ. Годную партию списывают с причиной Spoiled или Defective; "
            + "просроченную — без причины. Пустая партия — 409."),
        ["GET api/movements"] = ("Журнал движения", "Поставки, продажи и списания: время, продукт, количество, основание, сотрудник. batchId — история одной партии."),
    };

    public static IServiceCollection AddApiDocs(this IServiceCollection services) => services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Остаток API";
            document.Info.Description = "Партии со сроками годности, заказы со списанием, стоп-лист и горячие предложения. "
                + "Токен: POST /api/auth/login → кнопка Authorize → Bearer. Внешние системы присылают поставки по ключу X-Api-Key.";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[Bearer] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Токен из POST /api/auth/login.",
            };
            document.Components.SecuritySchemes[ApiKey] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = IntegrationKey.Header,
                Description = "Ключ интеграции ресторана: выдаёт управляющий в разделе «Ресторан». Открывает только POST /api/supplies/incoming.",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var description = context.Description;
            var key = $"{description.HttpMethod} {Route(description.RelativePath)}";
            if (Operations.TryGetValue(key, out var docs))
            {
                if (string.IsNullOrEmpty(operation.Summary)) operation.Summary = docs.Summary;
                if (string.IsNullOrEmpty(operation.Description)) operation.Description = docs.Description;
            }

            var metadata = description.ActionDescriptor.EndpointMetadata;
            var byKey = key == "POST api/supplies/incoming";
            if (metadata.OfType<IAllowAnonymous>().Any() && !byKey) return Task.CompletedTask;
            if (!metadata.OfType<IAuthorizeData>().Any() && !byKey) return Task.CompletedTask;
            // Несколько требований — «любое из»: поставку извне принимают и по токену, и по ключу.
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(Bearer, context.Document)] = [] });
            if (byKey) operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKey, context.Document)] = [] });
            return Task.CompletedTask;
        });
    });

    /// <summary>Шаблон адреса без ограничений параметров и косой черты в конце: api/orders/{id:guid}/ready → api/orders/{id}/ready.</summary>
    private static string Route(string? path) =>
        System.Text.RegularExpressions.Regex.Replace(path ?? "", @"\{(\w+)(:[^}]*)?\}", "{$1}").TrimEnd('/');

    public static void MapApiDocs(this WebApplication app)
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = Path.TrimStart('/');
            options.SwaggerEndpoint("/openapi/v1.json", "Остаток API v1");
            options.DocumentTitle = "Остаток — API";
        });
    }
}
