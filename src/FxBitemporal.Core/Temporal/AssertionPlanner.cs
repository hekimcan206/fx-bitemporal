namespace FxBitemporal.Core.Temporal;

/// <summary>A version to insert, as of the known-time instant at which the plan is executed.</summary>
public sealed record PlannedVersion(ValidPeriod Valid, RateFact Fact);

/// <summary>
/// The physical effect of one assertion: which currently believed versions stop being believed (their known period is
/// closed) and which versions start being believed. Nothing is ever deleted or modified in place.
/// </summary>
public sealed record AssertionPlan(IReadOnlyList<long> Close, IReadOnlyList<PlannedVersion> Insert)
{
    public static AssertionPlan NoOp { get; } = new([], []);

    public bool IsNoOp => Close.Count == 0 && Insert.Count == 0;
}

/// <summary>
/// The sequenced bitemporal update ("this fact holds for valid period P, as of now"), written as a pure function so the
/// PostgreSQL store and the in-memory model execute literally the same logic, and property tests can drive it directly.
/// SQL:2011 calls the valid-time half of this <c>UPDATE ... FOR PORTION OF</c>; PostgreSQL 17 has no such clause.
/// </summary>
public static class AssertionPlanner
{
    /// <param name="current">The versions of the series that are currently believed. Only those overlapping
    /// <paramref name="period"/> matter; others are ignored, so callers may pass a superset.</param>
    /// <param name="period">Valid period the fact is asserted for.</param>
    /// <param name="fact">The fact.</param>
    public static AssertionPlan Plan(IEnumerable<RateVersion> current, ValidPeriod period, RateFact fact)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fact);

        var overlapping = current
            .Where(v => v.Known.IsCurrent && v.Valid.Overlaps(period))
            .OrderBy(v => v.Valid.From)
            .ToList();

        // Re-ingesting a bulletin the system already believes must not create versions: otherwise every scheduled
        // re-run would churn known time and make "what changed?" queries useless.
        if (IsAlreadyBelieved(overlapping, period, fact))
        {
            return AssertionPlan.NoOp;
        }

        var close = new List<long>(overlapping.Count);
        var insert = new List<PlannedVersion>(overlapping.Count * 2 + 1);
        foreach (var version in overlapping)
        {
            close.Add(version.Id);

            // The parts of a superseded version outside the asserted period are still believed: re-insert them, with
            // their original fact, as of the same instant. This is what keeps unrelated dates' answers unchanged.
            foreach (var remnant in version.Valid.Minus(period))
            {
                insert.Add(new PlannedVersion(remnant, version.Fact));
            }
        }

        insert.Add(new PlannedVersion(period, fact));
        insert.Sort((a, b) => a.Valid.From.CompareTo(b.Valid.From));
        return new AssertionPlan(close, insert);
    }

    private static bool IsAlreadyBelieved(List<RateVersion> overlapping, ValidPeriod period, RateFact fact)
    {
        if (overlapping.Count == 0 || overlapping.Any(v => !v.Fact.SameRateAs(fact)))
        {
            return false;
        }

        // Same fact everywhere it overlaps: a no-op only if the overlapping versions cover the period without gaps.
        var cursor = period.From;
        foreach (var version in overlapping)
        {
            if (version.Valid.From > cursor)
            {
                return false;
            }

            if (version.Valid.To is null)
            {
                return true;
            }

            if (version.Valid.To > cursor)
            {
                cursor = version.Valid.To.Value;
            }

            if (period.To is { } end && cursor >= end)
            {
                return true;
            }
        }

        return false;
    }
}
