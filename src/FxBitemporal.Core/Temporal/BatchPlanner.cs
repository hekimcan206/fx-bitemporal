namespace FxBitemporal.Core.Temporal;

/// <summary>
/// Plans several observations of one series that become known at the <b>same</b> instant (one bulletin, one database
/// transaction). Applying them one by one at one instant would create versions that are superseded at the instant they
/// were created: empty known periods, which the schema rejects. So they are applied in memory in order and only the net
/// effect is written: initial versions that did not survive are closed, surviving new versions are inserted. Nobody could
/// ever observe the intermediate versions, so nothing is lost.
/// </summary>
public static class BatchPlanner
{
    /// <param name="current">Currently believed versions of the series overlapping the observations' dates, plus the
    /// first version starting after the latest observation (it bounds the last valid period).</param>
    /// <param name="observations">Observations of one series, applied in the given order (later wins).</param>
    public static AssertionPlan Plan(IReadOnlyCollection<RateVersion> current, IEnumerable<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(observations);

        var working = current.Where(v => v.Known.IsCurrent).ToList();
        var initialIds = working.Select(v => v.Id).ToHashSet();
        var placeholder = new KnownPeriod(DateTimeOffset.MinValue, null);
        long tempId = 0;

        foreach (var observation in observations)
        {
            if (ObservationPolicy.IsOverriddenByCorrection(working, observation))
            {
                continue;
            }

            var period = ObservationPolicy.PeriodFor(observation.Date, ObservationPolicy.NextStart(working, observation.Date));
            var plan = AssertionPlanner.Plan(working, period, observation.ToFact());
            if (plan.IsNoOp)
            {
                continue;
            }

            var closed = plan.Close.ToHashSet();
            working.RemoveAll(v => closed.Contains(v.Id));
            working.AddRange(plan.Insert.Select(p => new RateVersion(--tempId, observation.Series, p.Valid, placeholder, p.Fact)));
        }

        var close = initialIds.Where(id => working.All(v => v.Id != id)).Order().ToList();
        var insert = working.Where(v => v.Id < 0).OrderBy(v => v.Valid.From).Select(v => new PlannedVersion(v.Valid, v.Fact)).ToList();
        return close.Count == 0 && insert.Count == 0 ? AssertionPlan.NoOp : new AssertionPlan(close, insert);
    }
}
