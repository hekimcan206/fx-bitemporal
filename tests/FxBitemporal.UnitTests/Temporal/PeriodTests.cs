using FxBitemporal.Core;
using FxBitemporal.Core.Temporal;

namespace FxBitemporal.UnitTests.Temporal;

public sealed class PeriodTests
{
    private static DateOnly D(int day) => new(2026, 10, day);

    [Fact]
    public void A_period_is_half_open()
    {
        var period = new ValidPeriod(D(5), D(9));

        period.Contains(D(5)).Should().BeTrue();
        period.Contains(D(8)).Should().BeTrue();
        period.Contains(D(9)).Should().BeFalse();
        period.Contains(D(4)).Should().BeFalse();
    }

    [Fact]
    public void An_open_ended_period_contains_every_later_date()
    {
        var period = ValidPeriod.OpenFrom(D(5));

        period.Contains(new DateOnly(9999, 12, 31)).Should().BeTrue();
        period.IsOpenEnded.Should().BeTrue();
        period.ToString().Should().Be("[2026-10-05, infinity)");
    }

    [Fact]
    public void Empty_periods_are_rejected()
    {
        var act = () => new ValidPeriod(D(5), D(5));
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1, 5, 5, 9, false)] // adjacent: [1,5) and [5,9) share no day
    [InlineData(1, 6, 5, 9, true)]
    [InlineData(5, 9, 1, 6, true)]
    [InlineData(6, 7, 5, 9, true)] // contained
    public void Overlap_respects_the_exclusive_end(int a1, int a2, int b1, int b2, bool expected) =>
        new ValidPeriod(D(a1), D(a2)).Overlaps(new ValidPeriod(D(b1), D(b2))).Should().Be(expected);

    [Fact]
    public void Open_ended_periods_overlap_everything_after_their_start()
    {
        ValidPeriod.OpenFrom(D(5)).Overlaps(new ValidPeriod(D(20), D(21))).Should().BeTrue();
        ValidPeriod.OpenFrom(D(5)).Overlaps(new ValidPeriod(D(1), D(5))).Should().BeFalse();
    }

    [Fact]
    public void Minus_splits_around_a_hole()
    {
        ValidPeriod.OpenFrom(D(1)).Minus(new ValidPeriod(D(5), D(9)))
            .Should().Equal(new ValidPeriod(D(1), D(5)), ValidPeriod.OpenFrom(D(9)));
    }

    [Fact]
    public void Minus_keeps_only_the_left_part_when_the_hole_is_open_ended()
    {
        ValidPeriod.OpenFrom(D(1)).Minus(ValidPeriod.OpenFrom(D(5)))
            .Should().Equal(new ValidPeriod(D(1), D(5)));
    }

    [Fact]
    public void Minus_of_a_covering_period_is_empty_and_of_a_disjoint_one_is_identity()
    {
        new ValidPeriod(D(5), D(6)).Minus(new ValidPeriod(D(1), D(9))).Should().BeEmpty();
        new ValidPeriod(D(5), D(6)).Minus(new ValidPeriod(D(7), D(9))).Should().Equal(new ValidPeriod(D(5), D(6)));
    }

    [Fact]
    public void Known_periods_close_and_contain_half_open()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var period = new KnownPeriod(t0, null).CloseAt(t0.AddHours(1));

        period.Contains(t0).Should().BeTrue();
        period.Contains(t0.AddHours(1)).Should().BeFalse();
        period.IsCurrent.Should().BeFalse();
        period.ToString().Should().Be("[2026-11-01T09:00:00.000000Z, 2026-11-01T10:00:00.000000Z)");
        var act = () => new KnownPeriod(t0, t0);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("TP.DK.USD.A.YTL", "USD", QuoteSide.Buying, QuoteKind.Forex)]
    [InlineData("TP.DK.EUR.S.YTL", "EUR", QuoteSide.Selling, QuoteKind.Forex)]
    [InlineData("TP.DK.JPY.A.EF.YTL", "JPY", QuoteSide.Buying, QuoteKind.Banknote)]
    [InlineData("TP.DK.GBP.S.EF.YTL", "GBP", QuoteSide.Selling, QuoteKind.Banknote)]
    public void Series_codes_round_trip(string code, string currency, QuoteSide side, QuoteKind kind)
    {
        var key = SeriesKey.Parse(code);

        key.Should().Be(SeriesKey.For(currency, side, kind));
        (key.Currency, key.Side, key.Kind).Should().Be((currency, side, kind));
        key.ToString().Should().Be(code);
    }

    [Theory]
    [InlineData("TP.DK.usd.A.YTL")]
    [InlineData("TP.DK.USD.B.YTL")]
    [InlineData("TP.DK.USD.A.YTL;DROP")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_series_codes_are_rejected(string? code) =>
        SeriesKey.TryParse(code, out _).Should().BeFalse();

    [Theory]
    [InlineData("50.10780", "50.1078")]
    [InlineData("0.03710", "0.0371")]
    [InlineData("100.00", "100")]
    [InlineData("-0.00", "0")]
    [InlineData("1234567.5", "1234567.5")]
    public void Canonical_numbers_drop_scale(string input, string expected) =>
        Canonical.Number(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);
}
