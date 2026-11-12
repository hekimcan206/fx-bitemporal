using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace FxBitemporal.Benchmarks;

/// <summary>
/// The PostgreSQL store on a throwaway PostgreSQL 17 container, set up exactly like production (init script, migrations
/// as the owner, queries as the least-privilege role), loaded with a synthetic two-year history.
/// </summary>
internal static class StoreScenario
{
    private const int SeriesCount = 20;
    private const int BusinessDays = 520;
    private const double CorrectionRate = 0.01;
    private static readonly DateOnly Start = new(2024, 11, 4);
    private static readonly TimeSpan ContentionWindow = TimeSpan.FromSeconds(5);

    public static async Task<object> RunAsync(int pointQueries)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("fxbitemporal")
            .WithEnvironment("MIGRATOR_PASSWORD", "migrator-bench")
            .WithEnvironment("APP_PASSWORD", "app-bench")
            .WithResourceMapping(
                await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
                "/docker-entrypoint-initdb.d/01-roles.sh")
            .Build();
        await postgres.StartAsync();

        string As(string user, string password) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Username = user,
            Password = password,
            MaxPoolSize = 20,
            Timeout = 60,
        }.ConnectionString;

        await using (var migrator = PersistenceExtensions.CreateDataSource(As("fx_migrator", "migrator-bench")))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        await using var dataSource = PersistenceExtensions.CreateDataSource(As("fx_app", "app-bench"));
        var fenced = new PostgresRateStore(dataSource, new StoreOptions { Consistency = KnowledgeConsistency.Fenced });
        var naive = new PostgresRateStore(dataSource, new StoreOptions { Consistency = KnowledgeConsistency.Naive });

        // Synthetic series codes (TP.DK.SAA.A.YTL ...): valid by the schema's pattern, obviously not real currencies.
        var series = Enumerable.Range(0, SeriesCount)
            .Select(i => SeriesKey.Parse(string.Create(CultureInfo.InvariantCulture, $"TP.DK.S{(char)('A' + (i / 26))}{(char)('A' + (i % 26))}.A.YTL")))
            .ToArray();
        var dates = BusinessDates().ToArray();
        var random = new Random(20261112);

        // 1. Daily ingestion: one bulletin (20 series) per transaction, oldest first.
        Console.WriteLine($"loading {dates.Length} bulletins x {SeriesCount} series");
        var level = series.ToDictionary(s => s, _ => 30m + ((decimal)random.NextDouble() * 20m));
        var writeMs = new List<double>(dates.Length);
        var knownTimes = new List<DateTimeOffset>(dates.Length);
        var load = Stopwatch.StartNew();
        foreach (var date in dates)
        {
            var bulletin = series.Select(s =>
            {
                level[s] = Math.Round(level[s] * (1 + (decimal)((random.NextDouble() - 0.5) * 0.01)), 4);
                return new Observation(s, date, level[s], 1, RateSources.Fixture);
            }).ToList();
            var sw = Stopwatch.StartNew();
            var result = await fenced.RecordAsync(bulletin, "bench:ingest");
            writeMs.Add(sw.Elapsed.TotalMilliseconds);
            knownTimes.Add(result.KnownFrom!.Value);
        }

        var loadSeconds = load.Elapsed.TotalSeconds;

        // 2. Corrections: about 1 % of (series, bulletin) pairs, one per transaction, in random order.
        var targets = series.SelectMany(s => dates.Select(d => (Series: s, Date: d)))
            .Where(_ => random.NextDouble() < CorrectionRate)
            .OrderBy(_ => random.Next())
            .ToList();
        var correctionMs = new List<double>(targets.Count);
        foreach (var (s, date) in targets)
        {
            var current = (await fenced.AsOfAsync(s, date, null)).Version!;
            var sw = Stopwatch.StartNew();
            await fenced.RecordAsync(
                [new Observation(s, date, current.Value + 0.0123m, 1, RateSources.Correction, "synthetic benchmark correction")], "bench:correct");
            correctionMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        var (versions, tableBytes, indexBytes) = await SizeAsync(As("postgres", "postgres"));
        Console.WriteLine($"history: {versions} versions, table {tableBytes / 1024} KiB, indexes {indexBytes / 1024} KiB");

        // 3. Point reads ("rate for date D as known now / as known at K"), sequential, after a warm-up.
        async Task<Summary> PointAsync(PostgresRateStore store, bool pinned)
        {
            var samples = new List<double>(pointQueries);
            for (var i = -200; i < pointQueries; i++)
            {
                var s = series[random.Next(series.Length)];
                var d = dates[random.Next(dates.Length)];
                DateTimeOffset? k = pinned ? knownTimes[random.Next(knownTimes.Count)] : null;
                var sw = Stopwatch.StartNew();
                await store.AsOfAsync(s, d, k);
                if (i >= 0)
                {
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
            }

            return Summary.Of(samples);
        }

        var pointFenced = await PointAsync(fenced, pinned: false);
        var pointNaive = await PointAsync(naive, pinned: false);
        var pointPinned = await PointAsync(fenced, pinned: true);
        Console.WriteLine($"as-of now, fenced: {pointFenced}");
        Console.WriteLine($"as-of now, naive:  {pointNaive}");
        Console.WriteLine($"as-of pinned K:    {pointPinned}");

        // 4. The read behind one 12-position report: 12 series, one fence, one known time.
        var reportSeries = series.Take(12).ToArray();
        var reportMs = new List<double>();
        for (var i = -50; i < pointQueries / 4; i++)
        {
            var sw = Stopwatch.StartNew();
            await fenced.AsOfManyAsync(reportSeries, dates[random.Next(dates.Length)], null);
            if (i >= 0)
            {
                reportMs.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        var report = Summary.Of(reportMs);
        Console.WriteLine($"12-series report read: {report}");

        // 5. Contention: 4 readers ask "now" on one series while a writer keeps correcting that same series.
        var contentionFenced = await ContentionAsync(fenced, series[0], dates[^1]);
        var contentionNaive = await ContentionAsync(naive, series[1], dates[^1]);
        Console.WriteLine($"contended reads, fenced: {contentionFenced.Reads} ({contentionFenced.ReadsPerSecond}/s, writes {contentionFenced.WritesPerSecond}/s)");
        Console.WriteLine($"contended reads, naive:  {contentionNaive.Reads} ({contentionNaive.ReadsPerSecond}/s, writes {contentionNaive.WritesPerSecond}/s)");

        var plan = await ExplainAsync(As("fx_app", "app-bench"), series[3], dates[dates.Length / 2], knownTimes[knownTimes.Count / 3]);
        Console.WriteLine($"as-of plan: {JsonSerializer.Serialize(plan)}");

        return new
        {
            dataset = new { series = SeriesCount, bulletins = dates.Length, corrections = correctionMs.Count, versions, tableBytes, indexBytes },
            ingest = new
            {
                totalSeconds = Math.Round(loadSeconds, 2),
                bulletinWrite = Summary.Of(writeMs),
                first50 = Summary.Of(writeMs.Take(50).ToList()),
                last50 = Summary.Of(writeMs.TakeLast(50).ToList()),
            },
            correctionWrite = Summary.Of(correctionMs),
            asOfNowFenced = pointFenced,
            asOfNowNaive = pointNaive,
            asOfPinnedKnownTime = pointPinned,
            reportRead12Series = report,
            contentionFenced,
            contentionNaive,
            asOfPlan = plan,
        };
    }

    private static async Task<ContentionResult> ContentionAsync(PostgresRateStore store, SeriesKey series, DateOnly date)
    {
        await store.RecordAsync([new Observation(series, date, 40m, 1, RateSources.Fixture)], "bench");
        var reads = new ConcurrentBag<double>();
        var writes = new ConcurrentBag<double>();
        using var stop = new CancellationTokenSource(ContentionWindow);
        var writer = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await store.RecordAsync(
                    [new Observation(series, date, 40m + (++i * 0.0001m), 1, RateSources.Correction, "synthetic contention")], "bench");
                writes.Add(sw.Elapsed.TotalMilliseconds);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await store.AsOfAsync(series, date, null);
                reads.Add(sw.Elapsed.TotalMilliseconds);
            }
        }));
        await Task.WhenAll(readers.Append(writer));
        var seconds = ContentionWindow.TotalSeconds;
        return new ContentionResult(Summary.Of(reads), Summary.Of(writes), Math.Round(reads.Count / seconds, 1), Math.Round(writes.Count / seconds, 1));
    }

    private static IEnumerable<DateOnly> BusinessDates()
    {
        var count = 0;
        for (var d = Start; count < BusinessDays; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                count++;
                yield return d;
            }
        }
    }

    private static async Task<(long Versions, long TableBytes, long IndexBytes)> SizeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*), pg_table_size('fx.rate_version'), pg_indexes_size('fx.rate_version') FROM fx.rate_version", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static IEnumerable<string> IndexNames(JsonElement node)
    {
        if (node.TryGetProperty("Index Name", out var name) && name.GetString() is { } value)
        {
            yield return value;
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var nested in IndexNames(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static async Task<object> ExplainAsync(string connectionString, SeriesKey series, DateOnly date, DateTimeOffset knownAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
            SELECT * FROM fx.rate_version WHERE series = $1 AND valid_during @> $2 AND known_during @> $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = series.Code });
        command.Parameters.Add(new NpgsqlParameter { Value = date });
        command.Parameters.Add(new NpgsqlParameter { Value = knownAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement[0];
        var node = root.GetProperty("Plan");
        return new
        {
            nodeType = node.GetProperty("Node Type").GetString(),
            index = IndexNames(node).FirstOrDefault(),
            sharedHitBlocks = node.GetProperty("Shared Hit Blocks").GetInt64(),
            executionMs = root.GetProperty("Execution Time").GetDouble(),
        };
    }
}
