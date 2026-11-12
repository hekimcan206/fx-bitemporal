using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FxBitemporal.Core;

namespace FxBitemporal.Sources.Evds;

/// <summary>
/// The TCMB EVDS (Elektronik Veri Dağıtım Sistemi, electronic data delivery system) web service. Requests look like
/// <c>{base}series=TP.DK.USD.A.YTL-TP.DK.EUR.A.YTL&amp;startDate=01-10-2026&amp;endDate=31-10-2026&amp;type=json</c>, and
/// the personal API key travels in the <c>key</c> request header (never in the URL, where it would end up in logs).
/// </summary>
/// <remarks>
/// The response shape (<c>items[]</c> with <c>Tarih</c> as dd-MM-yyyy and one property per series, dots replaced by
/// underscores) follows the published usage guide and open-source clients; this project has no EVDS key, so the parser
/// is tested against recorded-shape fixtures, not live responses. See docs/adr/0004.
/// </remarks>
public sealed class EvdsSource(HttpClient http, string apiKey, IReadOnlyCollection<SeriesKey> series) : IRateSource
{
    public static readonly Uri DefaultBaseAddress = new("https://evds3.tcmb.gov.tr/igmevdsms-dis/");

    public const int MaxDays = 400;

    public string Name => RateSources.Evds;

    internal static Uri PathFor(IEnumerable<SeriesKey> series, DateOnly from, DateOnly toInclusive) =>
        new(string.Create(CultureInfo.InvariantCulture,
            $"series={string.Join('-', series.Select(s => s.Code))}&startDate={from:dd-MM-yyyy}&endDate={toInclusive:dd-MM-yyyy}&type=json"),
            UriKind.Relative);

    public async IAsyncEnumerable<Observation> FetchAsync(
        DateOnly from, DateOnly toInclusive, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RateSourceException("EVDS needs an API key (Sources:Evds:ApiKey). Use the tcmb-archive or fixture source without one.");
        }

        if (series.Count == 0)
        {
            yield break;
        }

        if (toInclusive < from || toInclusive.DayNumber - from.DayNumber >= MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(toInclusive), $"The range must be 1 to {MaxDays} days.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, PathFor(series, from, toInclusive));
        request.Headers.TryAddWithoutValidation("key", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new RateSourceException($"EVDS rejected the request ({(int)response.StatusCode}): check the API key header.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RateSourceException($"EVDS answered {(int)response.StatusCode}.");
        }

        foreach (var observation in await EvdsResponseParser.ParseAsync(body, series, Name, cancellationToken))
        {
            yield return observation;
        }
    }
}

/// <summary>Parses an EVDS <c>type=json</c> response into observations, skipping empty (non-business-day) cells.</summary>
public static class EvdsResponseParser
{
    public static async Task<IReadOnlyList<Observation>> ParseAsync(
        Stream json, IEnumerable<SeriesKey> series, string source, CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(json, new JsonDocumentOptions { MaxDepth = 8 }, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new RateSourceException("EVDS returned a body that is not JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new RateSourceException("EVDS response has no items array.");
            }

            var wanted = series.ToDictionary(s => s.Code.Replace('.', '_'), s => s, StringComparer.Ordinal);
            var result = new List<Observation>();
            foreach (var item in items.EnumerateArray())
            {
                var dateText = item.TryGetProperty("Tarih", out var tarih) ? tarih.GetString() : null;
                if (!DateOnly.TryParseExact(dateText, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    throw new RateSourceException($"EVDS item has an invalid Tarih '{dateText}'.");
                }

                foreach (var (column, key) in wanted)
                {
                    if (!item.TryGetProperty(column, out var cell) || ReadValue(cell, column, date) is not { } value)
                    {
                        continue;
                    }

                    result.Add(new Observation(key, date, value, UnitOf(key.Currency), source));
                }
            }

            return [.. result.OrderBy(o => o.Date).ThenBy(o => o.Series.Code, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// EVDS quotes the same units as the bulletin: per 100 for JPY, per 1 otherwise. Verified against the bulletin, not
    /// against live EVDS metadata (no key); kept in one place so it is easy to correct.
    /// </summary>
    public static int UnitOf(string currency) => currency == "JPY" ? 100 : 1;

    private static decimal? ReadValue(JsonElement cell, string column, DateOnly date)
    {
        decimal value;
        switch (cell.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                value = cell.GetDecimal();
                break;
            case JsonValueKind.String when string.IsNullOrWhiteSpace(cell.GetString()):
                return null;
            case JsonValueKind.String when decimal.TryParse(cell.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                break;
            default:
                throw new RateSourceException($"EVDS value for {column} on {date:yyyy-MM-dd} is not a decimal.");
        }

        return value > 0 ? value : throw new RateSourceException($"EVDS value for {column} on {date:yyyy-MM-dd} is not positive.");
    }
}
