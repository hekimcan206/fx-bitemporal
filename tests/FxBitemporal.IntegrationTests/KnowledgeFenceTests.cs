using System.Collections.Concurrent;
using System.Diagnostics;
using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using FxBitemporal.IntegrationTests.Infrastructure;

namespace FxBitemporal.IntegrationTests;

/// <summary>
/// Reproducibility under concurrency. A version's known time is stamped before its transaction commits; a reader whose
/// known time falls inside that commit window must either wait for the commit (fenced) or it gives an answer that
/// changes later (naive). These tests widen the window on purpose with a pre-commit hook.
/// </summary>
public sealed class KnowledgeFenceTests(FxEnvironment env)
{
    private static readonly DateOnly Date = new(2026, 12, 2);
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task Naive_stamping_lets_a_past_answer_change()
    {
        var store = env.CreateStore(KnowledgeConsistency.Naive);
        var series = SeriesKey.Parse("TP.DK.QNA.A.YTL");
        await store.RecordAsync([Obs(series, 10.0m)], "test");

        var (first, second) = await RaceAsync(store, series);

        first.Answer.Should().Be(10.0m, "the reader did not wait for the uncommitted correction");
        second.Should().Be(10.5m, "after the commit the same question, same known time, has a different answer");
        first.Elapsed.Should().BeLessThan(CommitDelay);
    }

    [Fact]
    public async Task The_fence_makes_the_reader_wait_and_the_answer_never_changes()
    {
        var store = env.CreateStore(KnowledgeConsistency.Fenced);
        var series = SeriesKey.Parse("TP.DK.QFE.A.YTL");
        await store.RecordAsync([Obs(series, 10.0m)], "test");

        var (first, second) = await RaceAsync(store, series);

        first.Answer.Should().Be(10.5m, "the reader's horizon is after the writer's instant, so it waited and saw the commit");
        second.Should().Be(first.Answer);
        first.Elapsed.Should().BeGreaterThan(CommitDelay / 2);
    }

    [Fact]
    public async Task Concurrent_writers_on_one_series_never_violate_the_invariant()
    {
        var series = SeriesKey.Parse("TP.DK.QCW.A.YTL");
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                var day = 1 + ((w * 7 + i * 3) % 20);
                await env.Store.RecordAsync([new Observation(series, new DateOnly(2026, 12, day), 20m + w + (i * 0.01m), 1, RateSources.Fixture)], $"writer-{w}");
            }
        }));

        await Task.WhenAll(writers);

        var current = new List<RateVersion>();
        for (var day = 1; day <= 25; day++)
        {
            if ((await env.Store.AsOfAsync(series, new DateOnly(2026, 12, day), null)).Version is { } v && current.All(c => c.Id != v.Id))
            {
                current.Add(v);
            }
        }

        current.Should().NotBeEmpty();
        for (var i = 0; i + 1 < current.Count; i++)
        {
            current[i].Valid.To.Should().Be(current[i + 1].Valid.From, "currently believed versions tile valid time");
        }

        current[^1].Valid.IsOpenEnded.Should().BeTrue();
    }

    /// <summary>
    /// Readers record (known time, answer) pairs while writers keep correcting; afterwards every pair is asked again.
    /// Under the fence not a single answer may differ.
    /// </summary>
    [Fact]
    public async Task Under_concurrent_corrections_every_fenced_answer_is_reproducible()
    {
        var series = SeriesKey.Parse("TP.DK.QRR.A.YTL");
        await env.Store.RecordAsync([Obs(series, 30m)], "test");
        var seen = new ConcurrentBag<(DateTimeOffset KnownAt, decimal? Value)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                await env.Store.RecordAsync(
                    [new Observation(series, Date, 30m + w + (++i * 0.0001m), 1, RateSources.Correction, "synthetic concurrent correction")], $"w{w}");
            }
        }));
        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (version, knownAt) = await env.Store.AsOfAsync(series, Date, null);
                seen.Add((knownAt, version?.Value));
            }
        }));
        await Task.WhenAll(writers.Concat(readers));

        var mismatches = 0;
        foreach (var (knownAt, value) in seen)
        {
            if ((await env.Store.AsOfAsync(series, Date, knownAt)).Version?.Value != value)
            {
                mismatches++;
            }
        }

        seen.Count.Should().BeGreaterThan(50);
        mismatches.Should().Be(0);
    }

    /// <summary>
    /// Starts a correction whose commit is held back, asks "now" while it is in flight, then asks again with the known
    /// time the first answer reported.
    /// </summary>
    private async Task<((decimal? Answer, TimeSpan Elapsed) First, decimal? Second)> RaceAsync(PostgresRateStore store, SeriesKey series)
    {
        var written = new TaskCompletionSource();
        var writer = store.RecordAsync(
            [new Observation(series, Date, 10.5m, 1, RateSources.Correction, "synthetic delayed correction")], "test",
            async () =>
            {
                written.SetResult();
                await Task.Delay(CommitDelay);
            },
            CancellationToken.None);

        await written.Task;
        await Task.Delay(100); // the reader's clock is now well past the writer's stamped instant
        var stopwatch = Stopwatch.StartNew();
        var (version, knownAt) = await store.AsOfAsync(series, Date, null);
        var elapsed = stopwatch.Elapsed;
        await writer;

        var again = (await store.AsOfAsync(series, Date, knownAt)).Version;
        return ((version?.Value, elapsed), again?.Value);
    }

    private static Observation Obs(SeriesKey series, decimal value) => new(series, Date, value, 1, RateSources.Fixture);
}
