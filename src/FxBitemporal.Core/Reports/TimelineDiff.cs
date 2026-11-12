using FxBitemporal.Core.Temporal;

namespace FxBitemporal.Core.Reports;

/// <summary>A business date whose answer differs between two known times.</summary>
public sealed record RateChange(DateOnly Date, RateVersion? Before, RateVersion? After);

/// <summary>
/// "Which answers changed between known time A and known time B?" Compares two valid-time timelines of one series day by
/// day. This is how a reader explains why two runs of the same report differ: every differing line maps to a change here.
/// </summary>
public static class TimelineDiff
{
    public const int MaxDays = 3_660;

    public static IReadOnlyList<RateChange> Compare(
        IReadOnlyList<RateVersion> before, IReadOnlyList<RateVersion> after, DateOnly from, DateOnly toInclusive)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (toInclusive < from || toInclusive.DayNumber - from.DayNumber >= MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(toInclusive), $"The range must be 1 to {MaxDays} days.");
        }

        var changes = new List<RateChange>();
        for (var date = from; date <= toInclusive; date = date.AddDays(1))
        {
            var a = Find(before, date);
            var b = Find(after, date);
            var same = (a, b) switch
            {
                (null, null) => true,
                ({ } x, { } y) => x.Fact.SameRateAs(y.Fact),
                _ => false,
            };
            if (!same)
            {
                changes.Add(new RateChange(date, a, b));
            }
        }

        return changes;
    }

    private static RateVersion? Find(IReadOnlyList<RateVersion> timeline, DateOnly date)
    {
        foreach (var version in timeline)
        {
            if (version.Valid.Contains(date))
            {
                return version;
            }
        }

        return null;
    }

    /// <summary>The valid range a diff over <paramref name="from"/>..<paramref name="toInclusive"/> must load.</summary>
    public static ValidPeriod Range(DateOnly from, DateOnly toInclusive) => new(from, toInclusive.AddDays(1));
}
