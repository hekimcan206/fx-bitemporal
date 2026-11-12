using FxBitemporal.Core.Temporal;

namespace FxBitemporal.Core;

/// <summary>
/// The payload of a version: the published rate for <see cref="Unit"/> units of foreign currency (JPY is quoted per
/// 100), the bulletin date it was published for, and where it came from. A correction is a fact whose source is
/// <see cref="RateSources.Correction"/>, with a mandatory reason.
/// </summary>
public sealed record RateFact(decimal Value, int Unit, DateOnly ObservedOn, string Source, string? Reason = null)
{
    /// <summary>Two facts describe the same real-world rate; provenance (source, reason) is deliberately ignored.</summary>
    public bool SameRateAs(RateFact other) =>
        other is not null && Value == other.Value && Unit == other.Unit && ObservedOn == other.ObservedOn;
}

/// <summary>One immutable row of the bitemporal table: a fact, where it is valid, and when the system believed it.</summary>
public sealed record RateVersion(long Id, SeriesKey Series, ValidPeriod Valid, KnownPeriod Known, RateFact Fact)
{
    public decimal Value => Fact.Value;

    public int Unit => Fact.Unit;
}

/// <summary>Well-known provenance values; mirrored by the CHECK constraint on <c>fx.rate_version.source</c>.</summary>
public static class RateSources
{
    public const string TcmbArchive = "tcmb-archive";
    public const string Evds = "evds";
    public const string Fixture = "fixture";
    public const string Correction = "correction";

    public static bool IsKnown(string source) => source is TcmbArchive or Evds or Fixture or Correction;
}

/// <summary>A rate as a source reports it for one bulletin date, before it enters the bitemporal store.</summary>
public sealed record Observation(SeriesKey Series, DateOnly Date, decimal Value, int Unit, string Source, string? Reason = null)
{
    public RateFact ToFact() => new(Value, Unit, Date, Source, Reason);
}
