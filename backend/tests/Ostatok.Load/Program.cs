// Нагрузочный прогон «Остатка» — только против локальной копии сервера (docker run, см. README «Нагрузка»).
// dotnet run --project backend/tests/Ostatok.Load -c Release -- http://127.0.0.1:8112 [stop-list] [race] [mixed]
// Сценарии:
//   stop-list — GET /api/stop-list официантом, 20 одновременных клиентов, 30 с: RPS и задержка; X-Cache покажет, с кэшем ли сервер
//               (замер «без кэша» — тот же сценарий против копии с Cache__Enabled=false);
//   race      — 50 одновременных заказов на последние 5 порций, три раунда: принято ровно 5, остальные 409, остаток не ушёл в минус;
//   mixed     — 60 с: официанты заказывают (20 в секунду), терминалы читают стоп-лист (10 в секунду),
//               кладовщик присылает поставку и принимает её (раз в 2 секунды); ответов 5xx быть не должно.
// Отчёты NBomber и сводка — в docs/load (или в папку из переменной LOAD_OUT).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

var baseUrl = new Uri(args.FirstOrDefault(a => a.StartsWith("http")) ?? "http://127.0.0.1:8112");
if (!baseUrl.IsLoopback)
{
    // Боевой сайт нагрузкой не проверяется: бесплатный тариф и общая база с реальными данными.
    Console.Error.WriteLine($"Только локальная копия сервера (127.0.0.1 / localhost), а не {baseUrl}.");
    return 1;
}
var chosen = args.Where(a => !a.StartsWith("http")).ToHashSet();
bool Run(string name) => chosen.Count == 0 || chosen.Contains(name);
var outDir = Path.GetFullPath(Environment.GetEnvironmentVariable("LOAD_OUT") ?? Path.Combine("docs", "load"));
Directory.CreateDirectory(outDir);

// Прокси из окружения обходится: сервер локальный. Соединения переиспользуются всеми клиентами.
var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1000, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

HttpClient Client(string? token = null)
{
    var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
    if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return client;
}

async Task<HttpClient> SignIn(string login)
{
    using var anonymous = Client();
    var response = await anonymous.PostAsJsonAsync("/api/auth/login", new { login, password = "demo2026" });
    response.EnsureSuccessStatusCode();
    return Client((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
}

async Task<JsonElement> Post(HttpClient client, string url, object body)
{
    var response = await client.PostAsJsonAsync(url, body, json);
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    return await response.Content.ReadFromJsonAsync<JsonElement>();
}

var health = await Client().GetFromJsonAsync<JsonElement>("/health");
Console.WriteLine($"Сервер {baseUrl}, версия {health.GetProperty("version")}");
var chef = await SignIn("demo");
var waiter = await SignIn("waiter");
var storekeeper = await SignIn("storekeeper");
var tag = DateTime.UtcNow.ToString("HHmmss");

async Task<(Guid Product, Guid Dish)> Menu(string name, decimal stock)
{
    var product = (await Post(chef, "/api/products", new { name = $"Нагрузка {name} {tag}", unit = "Kg", shelfLifeHours = 72, cost = 400 })).GetProperty("id").GetGuid();
    var dish = (await Post(chef, "/api/dishes", new { name = $"Блюдо {name} {tag}", price = 500, recipe = new[] { new { productId = product, quantity = 0.2m } } })).GetProperty("id").GetGuid();
    if (stock > 0) await Post(chef, "/api/supplies", new { number = $"НГ-{name}-{tag}", items = new[] { new { productId = product, quantity = stock } } });
    return (product, dish);
}

static string Ms(double value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
var summary = new StringBuilder();

// (а) Стоп-лист под чтением: 20 терминалов без пауз.
if (Run("stop-list"))
{
    var cache = new ConcurrentDictionary<string, int>();
    var scenario = Scenario.Create("stop_list", async _ =>
        {
            using var response = await waiter.GetAsync("/api/stop-list");
            var body = await response.Content.ReadAsByteArrayAsync();
            cache.AddOrUpdate(response.Headers.TryGetValues("X-Cache", out var v) ? v.First() : "-", 1, (_, n) => n + 1);
            var code = ((int)response.StatusCode).ToString();
            return response.IsSuccessStatusCode ? Response.Ok(statusCode: code, sizeBytes: body.Length) : Response.Fail(statusCode: code);
        })
        .WithWarmUpDuration(TimeSpan.FromSeconds(5))
        .WithLoadSimulations(Simulation.KeepConstant(copies: 20, during: TimeSpan.FromSeconds(30)));
    var stats = NBomberRunner.RegisterScenarios(scenario)
        .WithTestSuite("ostatok").WithTestName("stop-list")
        .WithReportFolder(Path.Combine(outDir, "nbomber", $"stop-list-{tag}"))
        .WithReportFileName($"stop-list-{tag}")
        .WithReportFormats(ReportFormat.Md, ReportFormat.Csv, ReportFormat.Txt)
        .Run();
    var s = stats.ScenarioStats.Single(x => x.ScenarioName == "stop_list");
    var mode = cache.ContainsKey("off") ? "без кэша (Cache__Enabled=false)" : "с кэшем";
    summary.AppendLine($"## Стоп-лист, {mode}")
        .AppendLine($"20 клиентов без пауз, 30 с. Запросов: {s.Ok.Request.Count} успешных, {s.Fail.Request.Count} ошибок.")
        .AppendLine($"RPS {Ms(s.Ok.Request.RPS)}, p50 {Ms(s.Ok.Latency.Percent50)} мс, p95 {Ms(s.Ok.Latency.Percent95)} мс, p99 {Ms(s.Ok.Latency.Percent99)} мс.")
        .AppendLine($"X-Cache (с прогревом): {string.Join(", ", cache.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"))}.")
        .AppendLine();
}

// (б) Последние 5 порций и 50 одновременных заказов — три раунда.
if (Run("race"))
{
    summary.AppendLine("## 50 одновременных заказов на последние 5 порций");
    for (var round = 1; round <= 3; round++)
    {
        var (product, dish) = await Menu($"гонка{round}", 1m);   // 1 кг по 0,2 на порцию — ровно 5 порций
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 50).Select(async _ =>
        {
            await start.Task;
            var watch = Stopwatch.StartNew();
            using var response = await waiter.PostAsJsonAsync("/api/orders", new { items = new[] { new { dishId = dish, quantity = 1 } } }, json);
            return ((int)response.StatusCode, watch.Elapsed.TotalMilliseconds);
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        var batches = await chef.GetFromJsonAsync<JsonElement>($"/api/stock/batches?productId={product}");
        var stock = (await chef.GetFromJsonAsync<JsonElement>("/api/stock")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        var movements = (await chef.GetFromJsonAsync<JsonElement>("/api/movements?take=500")).EnumerateArray()
            .Where(m => m.GetProperty("productId").GetGuid() == product && m.GetProperty("type").GetString() == "Order")
            .Sum(m => m.GetProperty("quantity").GetDecimal());
        var latencies = results.Select(r => r.Item2).Order().ToArray();
        var line = $"Раунд {round}: 201 — {results.Count(r => r.Item1 == 201)}, 409 — {results.Count(r => r.Item1 == 409)}, " +
                   $"другие — {results.Count(r => r.Item1 is not (201 or 409))}; списано заказами {movements} кг из 1; " +
                   $"партий с остатком {batches.GetArrayLength()}, остаток {stock.GetProperty("fresh").GetDecimal()} кг; " +
                   $"задержка p50 {Ms(latencies[latencies.Length / 2])} мс, p95 {Ms(latencies[(int)(latencies.Length * 0.95)])} мс, макс {Ms(latencies[^1])} мс.";
        Console.WriteLine(line);
        summary.AppendLine($"- {line}");
    }
    summary.AppendLine();
}

// (в) Смешанная нагрузка, 60 секунд.
if (Run("mixed"))
{
    var menu = new List<(Guid Product, Guid Dish)>();
    for (var i = 0; i < 5; i++) menu.Add(await Menu($"смена{i}", 20m));
    var codes = new ConcurrentDictionary<string, int>();
    void Count(string scenario, HttpStatusCode code) => codes.AddOrUpdate($"{scenario} {(int)code}", 1, (_, n) => n + 1);
    Response<object> Answer(HttpStatusCode code) =>
        (int)code < 500 ? Response.Ok(statusCode: ((int)code).ToString()) : Response.Fail(statusCode: ((int)code).ToString());

    var during = TimeSpan.FromSeconds(60);
    var waiters = Scenario.Create("waiters", async _ =>
        {
            var dish = menu[Random.Shared.Next(menu.Count)].Dish;
            using var response = await waiter.PostAsJsonAsync("/api/orders",
                new { items = new[] { new { dishId = dish, quantity = Random.Shared.Next(1, 3) } } }, json);
            Count("заказ", response.StatusCode);
            return Answer(response.StatusCode);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: during));
    var terminals = Scenario.Create("terminals", async _ =>
        {
            using var response = await waiter.GetAsync("/api/stop-list");
            await response.Content.ReadAsByteArrayAsync();
            Count("стоп-лист", response.StatusCode);
            return Answer(response.StatusCode);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: during));
    var number = 0;
    var keeper = Scenario.Create("storekeeper", async ctx =>
        {
            var send = await Step.Run("send", ctx, async () =>
            {
                using var response = await storekeeper.PostAsJsonAsync("/api/supplies/incoming", new
                {
                    number = $"НГ-вх-{tag}-{Interlocked.Increment(ref number)}",
                    supplier = "Нагрузочный поставщик",
                    items = menu.Select(m => new { productId = m.Product, quantity = 2m }).ToArray(),
                }, json);
                Count("поставка", response.StatusCode);
                return response.IsSuccessStatusCode
                    ? Response.Ok(payload: (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), statusCode: ((int)response.StatusCode).ToString())
                    : Response.Fail<Guid>(statusCode: ((int)response.StatusCode).ToString());
            });
            if (send.IsError) return send;
            return await Step.Run("accept", ctx, async () =>
            {
                using var response = await storekeeper.PostAsync($"/api/supplies/incoming/{send.Payload.Value}/accept", null);
                Count("приёмка", response.StatusCode);
                return Answer(response.StatusCode);
            });
        })
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.Inject(rate: 1, interval: TimeSpan.FromSeconds(2), during: during));

    var stats = NBomberRunner.RegisterScenarios(waiters, terminals, keeper)
        .WithTestSuite("ostatok").WithTestName("mixed")
        .WithReportFolder(Path.Combine(outDir, "nbomber", $"mixed-{tag}"))
        .WithReportFileName($"mixed-{tag}")
        .WithReportFormats(ReportFormat.Md, ReportFormat.Csv, ReportFormat.Txt)
        .Run();

    var stock = (await chef.GetFromJsonAsync<JsonElement>("/api/stock")).EnumerateArray()
        .Where(r => menu.Any(m => m.Product == r.GetProperty("productId").GetGuid())).Select(r => r.GetProperty("fresh").GetDecimal()).ToList();
    summary.AppendLine("## Смешанная нагрузка, 60 с");
    foreach (var name in new[] { "waiters", "terminals", "storekeeper" })
    {
        var s = stats.ScenarioStats.Single(x => x.ScenarioName == name);
        summary.AppendLine($"- {name}: запросов {s.Ok.Request.Count + s.Fail.Request.Count}, 5xx и сбоев {s.Fail.Request.Count}, " +
                           $"p50 {Ms(s.Ok.Latency.Percent50)} мс, p95 {Ms(s.Ok.Latency.Percent95)} мс.");
    }
    summary.AppendLine($"- Коды ответов: {string.Join(", ", codes.OrderBy(p => p.Key).Select(p => $"{p.Key} — {p.Value}"))}.")
        .AppendLine($"- Остаток пяти продуктов после прогона, кг: {string.Join(", ", stock)} (отрицательного нет: {stock.All(q => q >= 0)}).")
        .AppendLine();
}

var file = Path.Combine(outDir, $"summary-{DateTime.Now:yyyy-MM-dd-HHmmss}.md");
await File.WriteAllTextAsync(file,
    $"# Нагрузочный прогон {DateTime.Now:yyyy-MM-dd HH:mm}\n\nСервер {baseUrl}, версия {health.GetProperty("version")}. Замер на ноутбуке, Docker, одна копия сервера.\n\n{summary}");
Console.WriteLine(summary);
Console.WriteLine($"Сводка: {file}");
return 0;
