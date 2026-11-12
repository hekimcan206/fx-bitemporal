namespace FxBitemporal.Core.Temporal;

/// <summary>
/// Reference model of the bitemporal store: the same planner and observation policy as the PostgreSQL store, over a
/// list. Property tests run random histories against both and require identical answers; benchmarks use it to measure
/// the planner without I/O. Not thread-safe; callers pass the known-time instant explicitly.
/// </summary>
public sealed class InMemoryRateStore
{
    private readonly List<RateVersion> _versions = [];
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    private long _nextId = 1;

    public IReadOnlyList<RateVersion> All => _versions;

    /// <summary>Records a bulletin observation as of <paramref name="at"/> and returns the executed plan.</summary>
    public AssertionPlan Record(Observation observation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (at <= _lastChange)
        {
            // The PostgreSQL store enforces the same rule with a per-series advisory lock and a monotonic clock.
            throw new ArgumentOutOfRangeException(nameof(at), "Known time must strictly increase across changes.");
        }

        var current = _versions.Where(v => v.Series == observation.Series && v.Known.IsCurrent).ToList();
        var period = ObservationPolicy.PeriodFor(observation.Date, ObservationPolicy.NextStart(current, observation.Date));
        var plan = AssertionPlanner.Plan(current, period, observation.ToFact());
        Apply(observation.Series, plan, at);
        return plan;
    }

    public void Apply(SeriesKey series, AssertionPlan plan, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsNoOp)
        {
            return;
        }

        foreach (var id in plan.Close)
        {
            var index = _versions.FindIndex(v => v.Id == id);
            _versions[index] = _versions[index] with { Known = _versions[index].Known.CloseAt(at) };
        }

        foreach (var planned in plan.Insert)
        {
            _versions.Add(new RateVersion(_nextId++, series, planned.Valid, new KnownPeriod(at, null), planned.Fact));
        }

        _lastChange = at;
    }

    /// <summary>"What rate applied on <paramref name="date"/>, as the system knew it at <paramref name="knownAt"/>?"</summary>
    public RateVersion? AsOf(SeriesKey series, DateOnly date, DateTimeOffset knownAt) =>
        _versions.SingleOrDefault(v => v.Series == series && v.Valid.Contains(date) && v.Known.Contains(knownAt));

    /// <summary>Every version that ever covered <paramref name="date"/>, oldest belief first: the audit trail.</summary>
    public IReadOnlyList<RateVersion> History(SeriesKey series, DateOnly date) =>
        [.. _versions.Where(v => v.Series == series && v.Valid.Contains(date)).OrderBy(v => v.Known.From)];

    public IReadOnlyList<RateVersion> Timeline(SeriesKey series, ValidPeriod range, DateTimeOffset knownAt) =>
        [.. _versions.Where(v => v.Series == series && v.Valid.Overlaps(range) && v.Known.Contains(knownAt)).OrderBy(v => v.Valid.From)];
}
