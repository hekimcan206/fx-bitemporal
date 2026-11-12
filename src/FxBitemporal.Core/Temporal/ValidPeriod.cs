using System.Globalization;

namespace FxBitemporal.Core.Temporal;

/// <summary>
/// Valid time: the half-open business-date interval <c>[From, To)</c> during which a rate applies in the real world.
/// <c>To == null</c> means "until further notice", which is how the latest bulletin is carried over weekends and
/// holidays. Mirrors a PostgreSQL <c>daterange</c> with an inclusive lower and exclusive (possibly infinite) upper bound.
/// </summary>
public readonly record struct ValidPeriod
{
    public ValidPeriod(DateOnly from, DateOnly? to)
    {
        if (to is { } end && end <= from)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Empty valid period [{from:yyyy-MM-dd}, {end:yyyy-MM-dd})."), nameof(to));
        }

        From = from;
        To = to;
    }

    public DateOnly From { get; }

    /// <summary>Exclusive end; <c>null</c> is open-ended.</summary>
    public DateOnly? To { get; }

    public bool IsOpenEnded => To is null;

    public static ValidPeriod Day(DateOnly date) => new(date, date.AddDays(1));

    public static ValidPeriod OpenFrom(DateOnly date) => new(date, null);

    public bool Contains(DateOnly date) => date >= From && (To is null || date < To);

    public bool Overlaps(ValidPeriod other) =>
        (other.To is null || From < other.To) && (To is null || other.From < To);

    /// <summary>The parts of this period not covered by <paramref name="other"/>: zero, one or two periods.</summary>
    public IEnumerable<ValidPeriod> Minus(ValidPeriod other)
    {
        if (!Overlaps(other))
        {
            yield return this;
            yield break;
        }

        if (From < other.From)
        {
            yield return new ValidPeriod(From, other.From);
        }

        if (other.To is { } otherEnd && (To is null || otherEnd < To))
        {
            yield return new ValidPeriod(otherEnd, To);
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"[{From:yyyy-MM-dd}, {(To is { } t ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "infinity")})");
}
