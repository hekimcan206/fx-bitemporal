using BenchmarkDotNet.Attributes;
using FxBitemporal.Core;
using FxBitemporal.Core.Reports;
using FxBitemporal.Core.Temporal;
using FxBitemporal.Sources.Fixtures;
using FxBitemporal.Sources.Tcmb;

namespace FxBitemporal.Benchmarks;

/// <summary>
/// The CPU-side cost of one write and one report, without I/O: what the service does per bulletin and per request on
/// top of the database round trips measured by the store scenario.
/// </summary>
[MemoryDiagnoser]
public class MicroBenchmarks
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 13, 0, 0, TimeSpan.Zero);

    private byte[] _bulletinXml = [];
    private List<Observation> _nextBulletin = [];
    private Dictionary<SeriesKey, List<RateVersion>> _currentBySeries = [];
    private RevaluationRequest _request = null!;
    private Dictionary<SeriesKey, RateVersion> _rates = [];
    private List<RateVersion> _before = [];
    private List<RateVersion> _after = [];
    private DateOnly _from;
    private DateOnly _to;

    [GlobalSetup]
    public void Setup()
    {
        var dates = RecordedBulletinSource.Dates;
        using (var stream = RecordedBulletinSource.Open(dates[^1]))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            _bulletinXml = buffer.ToArray();
        }

        // All bulletins but the last are believed; the last one is the write being planned (the daily case).
        var store = new InMemoryRateStore();
        var at = T0;
        foreach (var date in dates.SkipLast(1))
        {
            store.RecordBatch([.. RecordedBulletinSource.Load(date).ToObservations(RateSources.Fixture)], at = at.AddMinutes(1));
        }

        _nextBulletin = [.. RecordedBulletinSource.Load(dates[^1]).ToObservations(RateSources.Fixture)];
        // What PostgresRateStore loads for that write: believed versions overlapping the date, plus the next one.
        _currentBySeries = _nextBulletin.Select(o => o.Series).Distinct().ToDictionary(
            s => s,
            s => store.All.Where(v => v.Series == s && v.Known.IsCurrent && v.Valid.Overlaps(ValidPeriod.Day(dates[^1]))).ToList());

        var valuation = dates[^2];
        string[] currencies = ["USD", "EUR", "GBP", "CHF", "JPY", "SEK", "NOK", "DKK", "CAD", "AUD", "SAR", "KWD"];
        _request = new RevaluationRequest(valuation, at, [.. currencies.Select((c, i) => new Position(c, 1000m * (i + 1)))]);
        _rates = currencies.Select(RevaluationRequest.SeriesFor)
            .Select(s => store.AsOf(s, valuation, at))
            .OfType<RateVersion>()
            .ToDictionary(v => v.Series);

        var usd = SeriesKey.Parse("TP.DK.USD.A.YTL");
        (_from, _to) = (dates[0], dates[^1]);
        var range = TimelineDiff.Range(_from, _to);
        _before = [.. store.Timeline(usd, range, T0.AddMinutes(10))];
        _after = [.. store.Timeline(usd, range, at)];
    }

    [Benchmark(Description = "parse one TCMB bulletin (XML, ~9 KB)")]
    public Bulletin ParseBulletin()
    {
        using var stream = new MemoryStream(_bulletinXml, writable: false);
        return TcmbBulletinParser.Parse(stream);
    }

    [Benchmark(Description = "plan one bulletin write (67 series)")]
    public int PlanBulletin()
    {
        var total = 0;
        foreach (var group in _nextBulletin.GroupBy(o => o.Series))
        {
            total += BatchPlanner.Plan(_currentBySeries[group.Key], group).Insert.Count;
        }

        return total;
    }

    [Benchmark(Description = "build + hash a 12-position revaluation report")]
    public string BuildReport() => RevaluationBuilder.Build(_request, _rates).Sha256;

    [Benchmark(Description = "diff a 40-day timeline between two known times")]
    public int DiffTimeline() =>
        TimelineDiff.Compare(_before, _after, _from, _to).Count;
}
