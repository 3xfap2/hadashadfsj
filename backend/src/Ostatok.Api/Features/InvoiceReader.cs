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

public sealed class OpenAiInvoiceReader(IHttpClientFactory clients, AiOptions options) : IInvoiceReader
{
    private const string Instructions = """
        Ты читаешь фото накладной поставщика или этикетки продукта для склада ресторана. Верни строки с продуктами.
        product — название. Если в справочнике ниже есть этот же продукт, верни название ТОЧНО как в справочнике; если нет — как написано на фото.
        quantity — количество числом в единицах справочника: граммы переведи в килограммы, миллилитры — в литры.
        unit — «кг», «л» или «шт».
        expires — дата «годен до» в виде ГГГГ-ММ-ДД, если она указана; иначе null.
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
                {"type":"json_schema","json_schema":{"name":"supply","strict":true,"schema":{"type":"object","additionalProperties":false,"required":["rows"],
                "properties":{"rows":{"type":"array","items":{"type":"object","additionalProperties":false,
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

        // Первая строка — заголовки, как в файле: разбор таблицы её пропускает.
        var table = new List<string[]> { new[] { "продукт", "количество", "единица", "срок хранения", "цена" } };
        foreach (var row in parsed.RootElement.GetProperty("rows").EnumerateArray().Take(SupplyImport.MaxRows))
        {
            string Number(string name) => row.GetProperty(name) is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal().ToString(CultureInfo.InvariantCulture) : "";
            table.Add([
                row.GetProperty("product").GetString() ?? "",
                Number("quantity"),
                row.GetProperty("unit").GetString() ?? "",
                Life(row, now),
                Number("sum"),
            ]);
        }
        return table;
    }

    /// <summary>Срок хранения текстом, как в файле. Дата «годен до» пересчитывается в часы от сейчас: годен до конца указанного дня.</summary>
    private static string Life(JsonElement row, DateTimeOffset now)
    {
        if (row.GetProperty("expires").GetString() is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
        {
            var hours = (int)Math.Floor((day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - now.UtcDateTime).TotalHours);
            return hours > 0 ? $"{hours} ч" : $"истёк {day:dd.MM.yyyy}";
        }
        return row.GetProperty("shelfLifeHours") is { ValueKind: JsonValueKind.Number } life ? $"{Math.Round(life.GetDecimal())} ч" : "";
    }
}
