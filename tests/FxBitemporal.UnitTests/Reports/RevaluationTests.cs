using System.Text;
using FxBitemporal.Core;
using FxBitemporal.Core.Reports;
using FxBitemporal.Core.Temporal;

namespace FxBitemporal.UnitTests.Reports;

public sealed class RevaluationTests
{
    private static readonly DateTimeOffset KnownAt = new(2026, 11, 3, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly ValuationDate = new(2026, 10, 31);

    private static RateVersion Version(long id, string currency, decimal value, int unit = 1) => new(
        id,
        RevaluationRequest.SeriesFor(currency),
        new ValidPeriod(new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 2)),
        new KnownPeriod(new DateTimeOffset(2026, 11, 2, 13, 45, 12, TimeSpan.Zero).AddTicks(3_456_780), null),
        new RateFact(value, unit, new DateOnly(2026, 10, 30), RateSources.TcmbArchive));

    private static readonly Dictionary<SeriesKey, RateVersion> Rates = new()
    {
        [RevaluationRequest.SeriesFor("USD")] = Version(11, "USD", 49.7747m),
        [RevaluationRequest.SeriesFor("EUR")] = Version(12, "EUR", 55.7149m),
        [RevaluationRequest.SeriesFor("JPY")] = Version(13, "JPY", 31.5605m, 100),
    };

    private static RevaluationRequest Request(params Position[] positions) => new(ValuationDate, KnownAt, positions);

    [Fact]
    public void A_report_serialises_to_the_golden_canonical_json()
    {
        var signed = RevaluationBuilder.Build(
            Request(new Position("USD", 125_000m), new("JPY", 3_000_000m), new("EUR", -40_000.50m), new("GBP", 10m)), Rates);

        Golden.Verify("revaluation-report.json", Encoding.UTF8.GetString(signed.CanonicalJson) + "\n");
        Golden.Verify("revaluation-report.sha256", signed.Sha256 + "\n");
    }

    [Fact]
    public void Lines_are_ordered_and_valued_per_unit_with_kurus_rounding()
    {
        var report = RevaluationBuilder.Build(Request(new Position("USD", 125_000m), new("JPY", 3_000_000m), new("EUR", -40_000.50m)), Rates).Report;

        report.Lines.Select(l => l.Currency).Should().Equal("EUR", "JPY", "USD");
        report.Lines.Single(l => l.Currency == "JPY").ValueTry.Should().Be(946_815m, "3,000,000 JPY x 31.5605 per 100");
        report.Lines.Single(l => l.Currency == "EUR").ValueTry.Should().Be(-2_228_623.86m, "-40,000.50 x 55.7149 = -2,228,623.857...");
        report.TotalTry.Should().Be(report.Lines.Sum(l => l.ValueTry!.Value));
        report.Complete.Should().BeTrue();
    }

    [Fact]
    public void A_missing_rate_is_reported_not_guessed()
    {
        var report = RevaluationBuilder.Build(Request(new Position("USD", 1m), new("GBP", 10m)), Rates).Report;

        var gbp = report.Lines.Single(l => l.Currency == "GBP");
        gbp.Status.Should().Be(RevaluationBuilder.StatusNoRate);
        (gbp.Rate, gbp.ValueTry, gbp.VersionId).Should().Be(((decimal?)null, (decimal?)null, (long?)null));
        report.Complete.Should().BeFalse();
        report.TotalTry.Should().Be(49.77m);
    }

    [Fact]
    public void Position_order_and_rate_scale_do_not_change_the_hash()
    {
        var a = RevaluationBuilder.Build(Request(new Position("USD", 1m), new("EUR", 2m)), Rates);
        var rescaled = Rates.ToDictionary(kv => kv.Key, kv => kv.Value with { Fact = kv.Value.Fact with { Value = kv.Value.Value * 1.0000m } });
        var b = RevaluationBuilder.Build(Request(new Position("EUR", 2.00m), new("USD", 1.0m)), rescaled);

        b.Sha256.Should().Be(a.Sha256);
    }

    [Fact]
    public void Known_time_is_part_of_the_report_identity()
    {
        var a = RevaluationBuilder.Build(Request(new Position("USD", 1m)), Rates);
        var b = RevaluationBuilder.Build(Request(new Position("USD", 1m)) with { KnownAt = KnownAt.AddTicks(10) }, Rates);
        var sameMicrosecond = RevaluationBuilder.Build(Request(new Position("USD", 1m)) with { KnownAt = KnownAt.AddTicks(9) }, Rates);

        b.Sha256.Should().NotBe(a.Sha256);
        sameMicrosecond.Sha256.Should().Be(a.Sha256, "known time has microsecond precision, like timestamptz");
    }

    [Theory]
    [InlineData("usd", 1, "positions.currency")]
    [InlineData("TRY", 1, "positions.currency")]
    [InlineData("USD", 0, "positions.amount")]
    [InlineData("USD", 2_000_000_000_000, "positions.amount")]
    public void Invalid_positions_are_rejected(string currency, decimal amount, string field) =>
        Request(new Position(currency, amount)).Validate().Should().ContainKey(field);

    [Fact]
    public void Duplicate_currencies_and_empty_or_oversized_requests_are_rejected()
    {
        Request(new Position("USD", 1m), new("USD", 2m)).Validate().Should().ContainKey("positions.currency");
        Request().Validate().Should().ContainKey("positions");
        Request([.. Enumerable.Range(0, RevaluationRequest.MaxPositions + 1).Select(_ => new Position("USD", 1m))])
            .Validate().Should().ContainKey("positions");
        Request(new Position("USD", 1m)).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_diff_names_exactly_the_dates_whose_answer_changed()
    {
        var store = new InMemoryRateStore();
        var usd = RevaluationRequest.SeriesFor("USD");
        var t = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        store.Record(new Observation(usd, new DateOnly(2026, 10, 29), 49.70m, 1, RateSources.Fixture), t.AddMinutes(1));
        store.Record(new Observation(usd, new DateOnly(2026, 10, 30), 49.77m, 1, RateSources.Fixture), t.AddMinutes(2));
        store.Record(new Observation(usd, new DateOnly(2026, 11, 2), 49.90m, 1, RateSources.Fixture), t.AddMinutes(3));
        var before = t.AddMinutes(4);
        store.Record(new Observation(usd, new DateOnly(2026, 10, 30), 49.80m, 1, RateSources.Correction, "synthetic"), t.AddMinutes(5));
        var after = t.AddMinutes(6);
        var range = TimelineDiff.Range(new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 3));

        var changes = TimelineDiff.Compare(
            store.Timeline(usd, range, before), store.Timeline(usd, range, after), range.From, new DateOnly(2026, 11, 3));

        changes.Select(c => (c.Date.Day, c.Before!.Value, c.After!.Value)).Should().Equal(
            (30, 49.77m, 49.80m), (31, 49.77m, 49.80m), (1, 49.77m, 49.80m));
        FluentActions.Invoking(() => TimelineDiff.Compare([], [], range.From, range.From.AddDays(-1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
