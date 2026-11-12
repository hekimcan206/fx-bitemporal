using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using FxBitemporal.Core;

namespace FxBitemporal.Sources.Tcmb;

/// <summary>
/// The public TCMB bulletin archive, <c>https://www.tcmb.gov.tr/kurlar/yyyyMM/ddMMyyyy.xml</c>. It needs no key and
/// serves one document per business day; weekends and public holidays answer 404, which means "no bulletin", not an
/// error. Retries and timeouts are the HttpClient pipeline's job (standard resilience handler in the API).
/// </summary>
public sealed class TcmbArchiveSource(HttpClient http) : IRateSource
{
    public static readonly Uri DefaultBaseAddress = new("https://www.tcmb.gov.tr/kurlar/");

    /// <summary>Upper bound per request so a scheduled run cannot turn into a crawl of the whole archive.</summary>
    public const int MaxDays = 400;

    public string Name => RateSources.TcmbArchive;

    public static Uri PathFor(DateOnly date) =>
        new(date.ToString("yyyyMM'/'ddMMyyyy'.xml'", CultureInfo.InvariantCulture), UriKind.Relative);

    public async Task<Bulletin?> GetBulletinAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(PathFor(date), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RateSourceException($"TCMB archive answered {(int)response.StatusCode} for {date:yyyy-MM-dd}.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bulletin = TcmbBulletinParser.Parse(body);

        // A misrouted or cached response for another day would silently assert rates on the wrong date.
        return bulletin.Date == date
            ? bulletin
            : throw new RateSourceException($"Requested the bulletin for {date:yyyy-MM-dd} but received {bulletin.Date:yyyy-MM-dd}.");
    }

    public async IAsyncEnumerable<Observation> FetchAsync(
        DateOnly from, DateOnly toInclusive, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (toInclusive < from || toInclusive.DayNumber - from.DayNumber >= MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(toInclusive), $"The range must be 1 to {MaxDays} days.");
        }

        for (var date = from; date <= toInclusive; date = date.AddDays(1))
        {
            // Saturdays and Sundays never have a bulletin; skipping them halves the requests.
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var bulletin = await GetBulletinAsync(date, cancellationToken);
            if (bulletin is null)
            {
                continue;
            }

            foreach (var observation in bulletin.ToObservations(Name))
            {
                yield return observation;
            }
        }
    }
}
