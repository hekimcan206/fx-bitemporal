using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using FxBitemporal.Core;

namespace FxBitemporal.Sources.Tcmb;

/// <summary>One currency row of a TCMB daily bulletin. Missing quotes (SDR has only forex buying) are <c>null</c>.</summary>
public sealed record BulletinRate(
    string Currency,
    int Unit,
    string Name,
    decimal? ForexBuying,
    decimal? ForexSelling,
    decimal? BanknoteBuying,
    decimal? BanknoteSelling);

/// <summary>A parsed TCMB "indicative exchange rates" bulletin (<c>kurlar/yyyyMM/ddMMyyyy.xml</c>).</summary>
public sealed record Bulletin(DateOnly Date, string BulletinNo, IReadOnlyList<BulletinRate> Rates)
{
    /// <summary>Flattens the bulletin into one observation per published quote, keyed by EVDS series code.</summary>
    public IEnumerable<Observation> ToObservations(string source)
    {
        foreach (var rate in Rates)
        {
            if (rate.ForexBuying is { } fb)
            {
                yield return new Observation(SeriesKey.For(rate.Currency, QuoteSide.Buying, QuoteKind.Forex), Date, fb, rate.Unit, source);
            }

            if (rate.ForexSelling is { } fs)
            {
                yield return new Observation(SeriesKey.For(rate.Currency, QuoteSide.Selling, QuoteKind.Forex), Date, fs, rate.Unit, source);
            }

            if (rate.BanknoteBuying is { } bb)
            {
                yield return new Observation(SeriesKey.For(rate.Currency, QuoteSide.Buying, QuoteKind.Banknote), Date, bb, rate.Unit, source);
            }

            if (rate.BanknoteSelling is { } bs)
            {
                yield return new Observation(SeriesKey.For(rate.Currency, QuoteSide.Selling, QuoteKind.Banknote), Date, bs, rate.Unit, source);
            }
        }
    }
}

/// <summary>
/// Parses the TCMB daily bulletin XML. The document is untrusted input from the network: DTDs are prohibited (no
/// entity expansion, no XXE), no external resolver is used, and the size is bounded.
/// </summary>
public static class TcmbBulletinParser
{
    /// <summary>A real bulletin is about 10 KB; anything far larger is not a bulletin.</summary>
    public const long MaxCharacters = 1_000_000;

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static Bulletin Parse(Stream xml)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(xml, Settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new RateSourceException($"The TCMB bulletin is not well-formed or not allowed XML: {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Tarih_Date")
        {
            throw new RateSourceException("The document is not a TCMB bulletin (root element Tarih_Date expected).");
        }

        if (!DateOnly.TryParseExact((string?)root.Attribute("Tarih"), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new RateSourceException("The bulletin has no valid Tarih (dd.MM.yyyy) attribute.");
        }

        var rates = new List<BulletinRate>();
        foreach (var currency in root.Elements("Currency"))
        {
            var code = ((string?)currency.Attribute("CurrencyCode") ?? (string?)currency.Attribute("Kod") ?? "").Trim();
            if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            {
                throw new RateSourceException($"Bulletin {date:yyyy-MM-dd} has an invalid currency code '{code}'.");
            }

            if (!int.TryParse(Text(currency, "Unit"), NumberStyles.None, CultureInfo.InvariantCulture, out var unit) || unit <= 0)
            {
                throw new RateSourceException($"Bulletin {date:yyyy-MM-dd}: {code} has an invalid Unit.");
            }

            rates.Add(new BulletinRate(
                code,
                unit,
                (Text(currency, "CurrencyName") ?? Text(currency, "Isim") ?? code).Trim(),
                Rate(currency, "ForexBuying", code, date),
                Rate(currency, "ForexSelling", code, date),
                Rate(currency, "BanknoteBuying", code, date),
                Rate(currency, "BanknoteSelling", code, date)));
        }

        if (rates.Count == 0)
        {
            throw new RateSourceException($"Bulletin {date:yyyy-MM-dd} contains no currencies.");
        }

        if (rates.GroupBy(r => r.Currency, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new RateSourceException($"Bulletin {date:yyyy-MM-dd} lists a currency twice.");
        }

        return new Bulletin(date, ((string?)root.Attribute("Bulten_No") ?? "").Trim(), rates);
    }

    private static string? Text(XElement parent, string name) => parent.Element(name)?.Value;

    private static decimal? Rate(XElement currency, string name, string code, DateOnly date)
    {
        var text = Text(currency, name)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new RateSourceException($"Bulletin {date:yyyy-MM-dd}: {code} {name} '{text}' is not a positive decimal.");
        }

        return value;
    }
}
