using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using FxBitemporal.Core;
using FxBitemporal.Core.Temporal;

namespace FxBitemporal.UnitTests.Temporal;

/// <summary>
/// The invariants of the model, over arbitrary histories of bulletins, late back-fills, re-ingestions and corrections
/// (dates in a 40-day window, values from a small set so that repeats and no-ops are common).
/// </summary>
public sealed class BitemporalPropertyTests
{
    public sealed record HistoryEntry(int SeriesIndex, int Day, int ValueIndex);

    private static readonly SeriesKey[] Series = [SeriesKey.Parse("TP.DK.USD.A.YTL"), SeriesKey.Parse("TP.DK.EUR.A.YTL")];
    private static readonly decimal[] Values = [41.10m, 41.25m, 41.30m, 47.80m, 47.95m];
    private static readonly DateOnly Origin = new(2026, 10, 1);
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Window = 40;

    public static Arbitrary<HistoryEntry[]> Histories() =>
        Gen.ArrayOf(
                from s in Gen.Choose(0, Series.Length - 1)
                from d in Gen.Choose(0, Window - 1)
                from v in Gen.Choose(0, Values.Length - 1)
                select new HistoryEntry(s, d, v))
            .Where(steps => steps.Length is > 0 and <= 60)
            .ToArbitrary();

    private static Observation ToObservation(HistoryEntry step) =>
        new(Series[step.SeriesIndex], Origin.AddDays(step.Day), Values[step.ValueIndex], 1, RateSources.Fixture);

    private static DateTimeOffset At(int i) => T0.AddSeconds(i + 1);

    private static IEnumerable<DateOnly> Probe() => Enumerable.Range(-1, Window + 3).Select(Origin.AddDays);

    [Property(Arbitrary = [typeof(BitemporalPropertyTests)], MaxTest = 300)]
    public bool No_two_versions_overlap_in_both_valid_and_known_time(HistoryEntry[] steps)
    {
        var store = Run(steps);
        var versions = store.All;
        for (var i = 0; i < versions.Count; i++)
        {
            for (var j = i + 1; j < versions.Count; j++)
            {
                var (a, b) = (versions[i], versions[j]);
                if (a.Series == b.Series && a.Valid.Overlaps(b.Valid) && KnownOverlap(a.Known, b.Known))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The headline guarantee: an answer given as of known time K is the same answer forever after.</summary>
    [Property(Arbitrary = [typeof(BitemporalPropertyTests)], MaxTest = 300)]
    public bool Every_past_answer_is_reproducible_after_any_later_history(HistoryEntry[] steps)
    {
        var store = new InMemoryRateStore();
        var snapshots = new List<(DateTimeOffset KnownAt, SeriesKey Series, DateOnly Date, RateFact? Fact)>();
        for (var i = 0; i < steps.Length; i++)
        {
            store.Record(ToObservation(steps[i]), At(i));
            var knownAt = At(i).AddMilliseconds(500);
            foreach (var series in Series)
            {
                foreach (var date in Probe())
                {
                    snapshots.Add((knownAt, series, date, store.AsOf(series, date, knownAt)?.Fact));
                }
            }
        }

        return snapshots.All(s => Equals(store.AsOf(s.Series, s.Date, s.KnownAt)?.Fact, s.Fact));
    }

    /// <summary>
    /// The current view equals a much simpler reference semantics: the last value recorded for each bulletin date,
    /// carried forward until the next bulletin date.
    /// </summary>
    [Property(Arbitrary = [typeof(BitemporalPropertyTests)], MaxTest = 300)]
    public bool The_current_view_is_last_write_wins_per_bulletin_carried_forward(HistoryEntry[] steps)
    {
        var store = Run(steps);
        var now = At(steps.Length + 1);
        foreach (var series in Series)
        {
            var latestPerDate = new SortedDictionary<DateOnly, decimal>();
            foreach (var observation in steps.Select(ToObservation).Where(o => o.Series == series))
            {
                latestPerDate[observation.Date] = observation.Value;
            }

            foreach (var date in Probe())
            {
                var expectedDate = latestPerDate.Keys.Where(d => d <= date).Cast<DateOnly?>().LastOrDefault();
                var actual = store.AsOf(series, date, now);
                if (expectedDate is null ? actual is not null
                    : actual is null || actual.Fact.ObservedOn != expectedDate || actual.Value != latestPerDate[expectedDate.Value])
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Property(Arbitrary = [typeof(BitemporalPropertyTests)], MaxTest = 300)]
    public bool Currently_believed_versions_tile_valid_time_without_gaps(HistoryEntry[] steps)
    {
        var store = Run(steps);
        foreach (var series in Series)
        {
            var current = store.All.Where(v => v.Series == series && v.Known.IsCurrent).OrderBy(v => v.Valid.From).ToList();
            if (current.Count == 0)
            {
                continue;
            }

            for (var i = 0; i + 1 < current.Count; i++)
            {
                if (current[i].Valid.To != current[i + 1].Valid.From)
                {
                    return false;
                }
            }

            if (!current[^1].Valid.IsOpenEnded)
            {
                return false;
            }
        }

        return true;
    }

    [Property(Arbitrary = [typeof(BitemporalPropertyTests)], MaxTest = 300)]
    public bool Replaying_the_whole_history_again_changes_nothing(HistoryEntry[] steps)
    {
        // Replaying in the original order re-asserts values that were superseded later, so only the final value per
        // bulletin date is replayed: that is what a scheduled re-ingestion of an unchanged source does.
        var store = Run(steps);
        var count = store.All.Count;
        var finalPerKey = steps.Select(ToObservation).GroupBy(o => (o.Series, o.Date)).Select(g => g.Last()).ToList();
        var i = steps.Length + 10;
        return finalPerKey.All(o => store.Record(o, At(i++)).IsNoOp) && store.All.Count == count;
    }

    private static InMemoryRateStore Run(HistoryEntry[] steps)
    {
        var store = new InMemoryRateStore();
        for (var i = 0; i < steps.Length; i++)
        {
            store.Record(ToObservation(steps[i]), At(i));
        }

        return store;
    }

    private static bool KnownOverlap(KnownPeriod a, KnownPeriod b) =>
        (b.To is null || a.From < b.To) && (a.To is null || b.From < a.To);
}
