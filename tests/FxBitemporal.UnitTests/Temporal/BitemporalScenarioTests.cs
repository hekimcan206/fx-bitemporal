using FxBitemporal.Core;
using FxBitemporal.Core.Temporal;

namespace FxBitemporal.UnitTests.Temporal;

/// <summary>The bitemporal behaviours a reader of the README should be able to check by hand, one per test.</summary>
public sealed class BitemporalScenarioTests
{
    private static readonly SeriesKey Usd = SeriesKey.Parse("TP.DK.USD.A.YTL");
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 13, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRateStore _store = new();
    private int _tick;

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private DateTimeOffset Next() => T0.AddMinutes(++_tick);

    private AssertionPlan Record(DateOnly date, decimal value, string source = RateSources.TcmbArchive, string? reason = null) =>
        _store.Record(new Observation(Usd, date, value, 1, source, reason), Next());

    [Fact]
    public void The_latest_bulletin_applies_until_further_notice()
    {
        Record(D(10, 30), 50.10m); // Friday
        var now = Next();

        _store.AsOf(Usd, D(10, 31), now)!.Value.Should().Be(50.10m, "Saturday uses Friday's bulletin");
        _store.AsOf(Usd, D(11, 1), now)!.Value.Should().Be(50.10m, "and so does Sunday");
        _store.AsOf(Usd, D(10, 29), now).Should().BeNull("nothing was published before the first bulletin");
    }

    [Fact]
    public void A_new_bulletin_ends_the_previous_one_without_rewriting_history()
    {
        Record(D(10, 30), 50.10m);
        var fridayKnown = Next();
        var plan = Record(D(11, 2), 50.20m); // Monday

        plan.Close.Should().HaveCount(1);
        plan.Insert.Select(p => p.Valid).Should().Equal(new ValidPeriod(D(10, 30), D(11, 2)), ValidPeriod.OpenFrom(D(11, 2)));

        var now = Next();
        _store.AsOf(Usd, D(11, 1), now)!.Value.Should().Be(50.10m);
        _store.AsOf(Usd, D(11, 3), now)!.Value.Should().Be(50.20m);

        // Before Monday's bulletin arrived, the system believed Friday's rate applied to Tuesday too. It still says so.
        _store.AsOf(Usd, D(11, 3), fridayKnown)!.Value.Should().Be(50.10m);
    }

    [Fact]
    public void A_correction_changes_later_answers_only()
    {
        Record(D(10, 30), 50.10m);
        Record(D(11, 2), 50.20m);
        var beforeCorrection = Next();

        Record(D(10, 30), 50.15m, RateSources.Correction, "synthetic: source revised Friday's value");
        var after = Next();

        _store.AsOf(Usd, D(10, 30), beforeCorrection)!.Value.Should().Be(50.10m, "what we knew then is preserved");
        _store.AsOf(Usd, D(10, 30), after)!.Value.Should().Be(50.15m);
        _store.AsOf(Usd, D(11, 1), after)!.Value.Should().Be(50.15m, "the corrected bulletin is also the weekend's rate");
        _store.AsOf(Usd, D(11, 2), after)!.Value.Should().Be(50.20m, "Monday's bulletin is untouched");
        _store.AsOf(Usd, D(11, 2), after)!.Fact.ObservedOn.Should().Be(D(11, 2));
    }

    [Fact]
    public void A_late_backfill_of_a_missed_bulletin_splits_the_carried_rate()
    {
        Record(D(11, 3), 50.30m); // Tuesday
        Record(D(11, 5), 50.50m); // Thursday; Wednesday's bulletin was missed
        var beforeBackfill = Next();
        Record(D(11, 4), 50.40m); // Wednesday arrives late
        var after = Next();

        _store.AsOf(Usd, D(11, 4), beforeBackfill)!.Value.Should().Be(50.30m);
        _store.AsOf(Usd, D(11, 4), after)!.Value.Should().Be(50.40m);
        _store.AsOf(Usd, D(11, 5), after)!.Value.Should().Be(50.50m);
        _store.Timeline(Usd, ValidPeriod.OpenFrom(D(11, 1)), after).Select(v => v.Valid.ToString())
            .Should().Equal("[2026-11-03, 2026-11-04)", "[2026-11-04, 2026-11-05)", "[2026-11-05, infinity)");
    }

    [Fact]
    public void Re_ingesting_a_known_bulletin_is_a_no_op()
    {
        Record(D(10, 30), 50.10m);
        Record(D(11, 2), 50.20m);
        var count = _store.All.Count;

        Record(D(10, 30), 50.10m, RateSources.Fixture).IsNoOp.Should().BeTrue("same rate, different provenance");
        Record(D(11, 2), 50.20m).IsNoOp.Should().BeTrue();
        _store.All.Should().HaveCount(count);
    }

    [Fact]
    public void History_lists_every_belief_about_a_date_in_known_time_order()
    {
        Record(D(10, 30), 50.10m);
        Record(D(11, 2), 50.20m);
        Record(D(10, 30), 50.15m, RateSources.Correction, "synthetic");

        var history = _store.History(Usd, D(10, 31));

        history.Select(v => (v.Value, v.Valid.ToString(), v.Known.IsCurrent)).Should().Equal(
            (50.10m, "[2026-10-30, infinity)", false),
            (50.10m, "[2026-10-30, 2026-11-02)", false),
            (50.15m, "[2026-10-30, 2026-11-02)", true));
    }

    [Fact]
    public void Known_time_must_increase()
    {
        _store.Record(new Observation(Usd, D(10, 30), 50m, 1, RateSources.Fixture), T0);
        var act = () => _store.Record(new Observation(Usd, D(11, 2), 51m, 1, RateSources.Fixture), T0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_planner_ignores_versions_outside_the_asserted_period()
    {
        var unrelated = new RateVersion(
            7, Usd, new ValidPeriod(D(10, 1), D(10, 2)), new KnownPeriod(T0, null), new RateFact(49m, 1, D(10, 1), RateSources.Fixture));

        var plan = AssertionPlanner.Plan([unrelated], ValidPeriod.OpenFrom(D(10, 5)), new RateFact(50m, 1, D(10, 5), RateSources.Fixture));

        plan.Close.Should().BeEmpty();
        plan.Insert.Should().ContainSingle().Which.Valid.Should().Be(ValidPeriod.OpenFrom(D(10, 5)));
    }

    [Fact]
    public void A_same_rate_assertion_over_a_gap_is_not_a_no_op()
    {
        var fact = new RateFact(50m, 1, D(10, 5), RateSources.Fixture);
        var piece = new RateVersion(1, Usd, new ValidPeriod(D(10, 5), D(10, 6)), new KnownPeriod(T0, null), fact);

        AssertionPlanner.Plan([piece], new ValidPeriod(D(10, 5), D(10, 8)), fact).IsNoOp.Should().BeFalse();
        AssertionPlanner.Plan([piece], new ValidPeriod(D(10, 5), D(10, 6)), fact).IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void A_re_ingested_bulletin_does_not_undo_a_correction()
    {
        Record(D(10, 30), 50.10m);
        Record(D(10, 30), 50.15m, RateSources.Correction, "synthetic: published value was wrong");
        var count = _store.All.Count;

        Record(D(10, 30), 50.10m).IsNoOp.Should().BeTrue("the feed still publishes the original value");
        _store.All.Should().HaveCount(count);
        _store.AsOf(Usd, D(10, 31), Next())!.Value.Should().Be(50.15m);

        Record(D(10, 30), 50.10m, RateSources.Correction, "synthetic: correction withdrawn").IsNoOp.Should().BeFalse();
        _store.AsOf(Usd, D(10, 31), Next())!.Value.Should().Be(50.10m);
    }

    [Fact]
    public void A_correction_only_shields_its_own_bulletin_date()
    {
        Record(D(10, 30), 50.10m);
        Record(D(10, 30), 50.15m, RateSources.Correction, "synthetic: published value was wrong");

        Record(D(11, 2), 50.20m).IsNoOp.Should().BeFalse();
        _store.AsOf(Usd, D(11, 2), Next())!.Value.Should().Be(50.20m);
        _store.AsOf(Usd, D(10, 31), Next())!.Value.Should().Be(50.15m);
    }

    [Fact]
    public void The_batch_planner_applies_the_same_correction_precedence()
    {
        var store = new InMemoryRateStore();
        store.Record(new Observation(Usd, D(10, 30), 50.10m, 1, RateSources.Fixture), Next());
        store.Record(new Observation(Usd, D(10, 30), 50.15m, 1, RateSources.Correction, "synthetic"), Next());
        var count = store.All.Count;

        store.RecordBatch([new Observation(Usd, D(10, 30), 50.10m, 1, RateSources.TcmbArchive)], Next());

        store.All.Should().HaveCount(count);
    }
}
