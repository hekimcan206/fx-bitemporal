namespace FxBitemporal.Core.Temporal;

/// <summary>
/// Maps a bulletin onto valid time. TCMB publishes on business days only, and the latest bulletin applies until the
/// next one (weekends, public holidays, half days). So a bulletin for date D is asserted for
/// <c>[D, next bulletin date)</c>, where "next" is the earliest currently believed version that starts after D, or
/// open-ended when D is the newest bulletin.
/// </summary>
/// <remarks>
/// The same rule serves ingestion, late back-fills of a missed bulletin, and corrections of a published value: all three
/// are "the bulletin for D said X", which is why the API has no free-form valid-period write.
/// </remarks>
public static class ObservationPolicy
{
    /// <param name="date">Bulletin date.</param>
    /// <param name="nextStart">Smallest valid-from date greater than <paramref name="date"/> among currently believed
    /// versions of the series, or <c>null</c> if there is none.</param>
    public static ValidPeriod PeriodFor(DateOnly date, DateOnly? nextStart) =>
        nextStart is { } next && next > date
            ? new ValidPeriod(date, next)
            : ValidPeriod.OpenFrom(date);

    public static DateOnly? NextStart(IEnumerable<RateVersion> current, DateOnly date)
    {
        DateOnly? next = null;
        foreach (var version in current)
        {
            if (version.Known.IsCurrent && version.Valid.From > date && (next is null || version.Valid.From < next))
            {
                next = version.Valid.From;
            }
        }

        return next;
    }
}
