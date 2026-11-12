using System.Globalization;

namespace FxBitemporal.Core.Temporal;

/// <summary>
/// Known (transaction) time: the half-open interval <c>[From, To)</c> during which the system believed a version.
/// <c>To == null</c> means the version is still believed. Mirrors a PostgreSQL <c>tstzrange</c>; timestamps come from
/// the database clock only, never from application servers, so there is a single time line to reproduce against.
/// </summary>
public readonly record struct KnownPeriod
{
    public KnownPeriod(DateTimeOffset from, DateTimeOffset? to)
    {
        if (to is { } end && end <= from)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Empty known period [{from:O}, {end:O})."), nameof(to));
        }

        From = from;
        To = to;
    }

    public DateTimeOffset From { get; }

    public DateTimeOffset? To { get; }

    public bool IsCurrent => To is null;

    public bool Contains(DateTimeOffset instant) => instant >= From && (To is null || instant < To);

    public KnownPeriod CloseAt(DateTimeOffset instant) => new(From, instant);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"[{Canonical.Timestamp(From)}, {(To is { } t ? Canonical.Timestamp(t) : "infinity")})");
}
