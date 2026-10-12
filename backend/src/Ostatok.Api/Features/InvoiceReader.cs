using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed class AiOptions
{
    /// <summary>Ключ сервиса распознавания. Пусто — загрузка поставки по фото выключена, файлы работают как раньше.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}

/// <summary>
/// Читает фото накладной или этикетки и возвращает ту же таблицу, что и файл поставки:
/// продукт, количество, единица, срок хранения, сумма по строке. Дальше строки проходят обычную проверку
/// по справочнику, а человек подтверждает их перед приёмкой — сама модель на склад ничего не кладёт.
/// </summary>
public interface IInvoiceReader
{
    bool Enabled { get; }
    Task<List<string[]>> ReadAsync(byte[] image, string mime, IReadOnlyCollection<Product> products, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Распознавание платное, а вход в демо-ресторан открыт всем: без предела ключ можно было бы израсходовать за вечер.
/// Не больше <see cref="PerHour"/> снимков в час на ресторан.
/// </summary>
public sealed class PhotoQuota(TimeProvider time)
{
    public const int PerHour = 20;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _used = new();

    public bool TryTake(Guid restaurant)
    {
        var now = time.GetUtcNow();
        var used = _used.GetOrAdd(restaurant, _ => new Queue<DateTimeOffset>());
        lock (used)
        {
            while (used.Count > 0 && used.Peek() <= now.AddHours(-1)) used.Dequeue();
            if (used.Count >= PerHour) return false;
            used.Enqueue(now);
            return true;
        }
    }
}

public sealed class OpenAiInvoiceReader(IHttpClientFactory clients, AiOptions options) : IInvoiceReader
{
    private const string Instructions = """
        Ты читаешь фото накладной поставщика или этикетки продукта для склада ресторана. Верни строки с продуктами.
        product — название. Если в справочнике ниже есть этот же продукт, верни название ТОЧНО как в справочнике; если нет — как написано на фото.
        quantity — количество числом в единицах справочника: граммы переведи в килограммы, миллилитры — в литры.
        unit — «кг», «л» или «шт».
        expires — дата «годен до» в виде ГГГГ-ММ-ДД, только если у этой строки прямо написано «годен до», «срок годности до», «употребить до» или стоит отдельная колонка с такой датой; иначе null.
        Дата самой накладной, дата отгрузки и дата изготовления — это НЕ срок годности: в expires их не ставь.
        documentDate — дата самой накладной (документа) в виде ГГГГ-ММ-ДД, если она есть; иначе null.
        shelfLifeHours — срок хранения в часах, если указан именно срок, а не дата; иначе null.
        sum — сумма по строке в рублях; если указана только цена за единицу, умножь её на количество; если денег нет — null.
        Ничего не выдумывай: чего нет на фото — null. Строки не о продуктах (итоги, подписи, реквизиты) пропускай.
        """;

    public bool Enabled => options.ApiKey != "";

    public async Task<List<string[]>> ReadAsync(byte[] image, string mime, IReadOnlyCollection<Product> products, DateTimeOffset now, CancellationToken ct)
    {
        var catalogue = string.Join("\n", products.Select(p => $"- {p.Name} ({(p.Unit == Unit.Kg ? "кг" : p.Unit == Unit.L ? "л" : "шт")})"));
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = $"{Instructions}\nСправочник продуктов:\n{catalogue}" },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(image)}", ["detail"] = "high" },
                    }),
                }),
            ["response_format"] = JsonNode.Parse("""
                {"type":"json_schema","json_schema":{"name":"supply","strict":true,"schema":{"type":"object","additionalProperties":false,"required":["documentDate","rows"],
                "properties":{"documentDate":{"type":["string","null"]},"rows":{"type":"array","items":{"type":"object","additionalProperties":false,
                "required":["product","quantity","unit","expires","shelfLifeHours","sum"],
                "properties":{"product":{"type":"string"},"quantity":{"type":["number","null"]},"unit":{"type":["string","null"]},
                "expires":{"type":["string","null"]},"shelfLifeHours":{"type":["number","null"]},"sum":{"type":["number","null"]}}}}}}}}
                """),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await clients.CreateClient().SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var text = answer.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
        using var parsed = JsonDocument.Parse(text);

        // Модель иногда принимает дату накладной за срок годности: такой «срок» отбрасывается, и строка получит норматив продукта.
        var documentDate = parsed.RootElement.GetProperty("documentDate").GetString();
        // Первая строка — заголовки, как в файле: разбор таблицы её пропускает.
        var table = new List<string[]> { new[] { "продукт", "количество", "единица", "срок хранения", "цена" } };
        foreach (var row in parsed.RootElement.GetProperty("rows").EnumerateArray().Take(SupplyImport.MaxRows))
        {
            string Number(string name) => row.GetProperty(name) is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal().ToString(CultureInfo.InvariantCulture) : "";
            table.Add([
                row.GetProperty("product").GetString() ?? "",
                Number("quantity"),
                row.GetProperty("unit").GetString() ?? "",
                Life(row, now, documentDate),
                Number("sum"),
            ]);
        }
        return table;
    }

    /// <summary>Срок хранения текстом, как в файле. Дата «годен до» пересчитывается в часы от сейчас: годен до конца указанного дня в часовом поясе ресторана.</summary>
    private static string Life(JsonElement row, DateTimeOffset now, string? documentDate)
    {
        if (row.GetProperty("expires").GetString() is { } date && date != documentDate && DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
        {
            // «Годен до 15.10» — до конца этого дня по часам ресторана: now приходит уже в его часовом поясе.
            var hours = (int)Math.Floor((new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), now.Offset) - now).TotalHours);
            return hours > 0 ? $"{hours} ч" : $"истёк {day:dd.MM.yyyy}";
        }
        return row.GetProperty("shelfLifeHours") is { ValueKind: JsonValueKind.Number } life ? $"{Math.Round(life.GetDecimal())} ч" : "";
    }
}
