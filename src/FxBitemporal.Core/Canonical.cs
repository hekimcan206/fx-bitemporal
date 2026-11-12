using System.Globalization;

namespace FxBitemporal.Core;

/// <summary>
/// One textual form per value, so that the same report always serialises to the same bytes (and the same hash)
/// regardless of culture, decimal scale or offset: <c>50.10780</c> from one source and <c>50.1078</c> from another
/// are the same rate and must hash the same.
/// </summary>
public static class Canonical
{
    /// <summary>Invariant culture, no exponent, trailing zeros removed (<c>0.03710</c> becomes <c>0.0371</c>).</summary>
    public static string Number(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text == "-0" ? "0" : text;
    }

    /// <summary>UTC with microseconds, the precision of a PostgreSQL <c>timestamptz</c>.</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Truncates to whole microseconds so an in-memory instant compares equal to its database round trip.</summary>
    public static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);
}
