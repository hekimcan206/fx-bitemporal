using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Running;
using FxBitemporal.Benchmarks;

// Usage:
//   dotnet run -c Release --project benchmarks/FxBitemporal.Benchmarks -- micro    (BenchmarkDotNet, no I/O)
//   dotnet run -c Release --project benchmarks/FxBitemporal.Benchmarks -- store    (Testcontainers PostgreSQL 17)
//   dotnet run -c Release --project benchmarks/FxBitemporal.Benchmarks -- http     (against `docker compose up`)
// Results are written to benchmark-results/<scenario>.json together with a description of the environment.
var mode = args.FirstOrDefault() ?? "store";
if (mode == "micro")
{
    BenchmarkRunner.Run<MicroBenchmarks>(args: [.. args.Skip(1)]);
    return;
}

var outDir = Env("OUT_DIR", "benchmark-results");
var samples = int.Parse(Env("SAMPLES", "2000"), CultureInfo.InvariantCulture);
Directory.CreateDirectory(outDir);
var environment = new
{
    os = RuntimeInformation.OSDescription,
    runtime = RuntimeInformation.FrameworkDescription,
    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
    logicalCores = Environment.ProcessorCount,
};
Console.WriteLine($"environment: {JsonSerializer.Serialize(environment)}");

object result = mode switch
{
    "store" => await StoreScenario.RunAsync(samples),
    "http" => await HttpAsync(new Uri(Env("API_URL", "http://127.0.0.1:58744/")), samples),
    _ => throw new ArgumentException($"Unknown scenario '{mode}'. Use micro, store or http."),
};

await File.WriteAllTextAsync(Path.Combine(outDir, $"{mode}.json"), JsonSerializer.Serialize(
    new { scenario = mode, measuredAt = DateTimeOffset.UtcNow, environment, result },
    new JsonSerializerOptions { WriteIndented = true }));

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

// Sequential requests through the full stack (Kestrel, validation, fence, PostgreSQL, canonical JSON) from the host.
static async Task<object> HttpAsync(Uri api, int count)
{
    using var http = new HttpClient { BaseAddress = api };
    var positions = new[] { new { currency = "USD", amount = 125000m }, new { currency = "EUR", amount = -40000.50m }, new { currency = "JPY", amount = 3000000m } };

    async Task<Summary> MeasureAsync(Func<int, Task<HttpResponseMessage>> call)
    {
        var latencies = new List<double>(count);
        for (var i = -100; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await call(i);
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync();
            if (i >= 0)
            {
                latencies.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        return Summary.Of(latencies);
    }

    var rate = await MeasureAsync(i => http.GetAsync(new Uri(
        string.Create(CultureInfo.InvariantCulture, $"api/v1/rates/TP.DK.USD.A.YTL?date=2026-10-{1 + (Math.Abs(i) % 30):00}"), UriKind.Relative)));
    var report = await MeasureAsync(_ => http.PostAsJsonAsync(new Uri("api/v1/reports/revaluation", UriKind.Relative),
        new { valuationDate = "2026-10-31", positions }));
    Console.WriteLine($"GET rate as of now:      {rate}");
    Console.WriteLine($"POST 3-position report:  {report}");
    return new { getRateAsOfNow = rate, postRevaluationReport3Positions = report };
}
