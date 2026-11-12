using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace FxBitemporal.Core;

/// <summary>Which side of the quote a series carries.</summary>
public enum QuoteSide
{
    Buying,
    Selling,
}

/// <summary>Forex rates settle electronic transfers; banknote rates apply to cash.</summary>
public enum QuoteKind
{
    Forex,
    Banknote,
}

/// <summary>
/// A TCMB indicative exchange-rate series, identified by its EVDS code, for example <c>TP.DK.USD.A.YTL</c> (USD forex
/// buying) or <c>TP.DK.EUR.S.EF.YTL</c> (EUR banknote selling). The EVDS code is the canonical id because it is the
/// one public, stable name for a series; the daily XML bulletin is mapped onto it.
/// </summary>
public readonly partial record struct SeriesKey
{
    private SeriesKey(string code, string currency, QuoteSide side, QuoteKind kind)
    {
        Code = code;
        Currency = currency;
        Side = side;
        Kind = kind;
    }

    public string Code { get; }

    /// <summary>ISO 4217 code of the foreign currency; the quote currency is always TRY.</summary>
    public string Currency { get; }

    public QuoteSide Side { get; }

    public QuoteKind Kind { get; }

    public static SeriesKey For(string currency, QuoteSide side, QuoteKind kind)
    {
        if (currency is null || !CurrencyPattern().IsMatch(currency))
        {
            throw new ArgumentException($"'{currency}' is not a three-letter upper-case currency code.", nameof(currency));
        }

        var code = $"TP.DK.{currency}.{(side == QuoteSide.Buying ? "A" : "S")}{(kind == QuoteKind.Banknote ? ".EF" : "")}.YTL";
        return new SeriesKey(code, currency, side, kind);
    }

    public static bool TryParse([NotNullWhen(true)] string? code, out SeriesKey key)
    {
        key = default;
        if (code is null)
        {
            return false;
        }

        var match = CodePattern().Match(code);
        if (!match.Success)
        {
            return false;
        }

        key = new SeriesKey(
            code,
            match.Groups["ccy"].Value,
            match.Groups["side"].Value == "A" ? QuoteSide.Buying : QuoteSide.Selling,
            match.Groups["ef"].Success ? QuoteKind.Banknote : QuoteKind.Forex);
        return true;
    }

    public static SeriesKey Parse(string code) =>
        TryParse(code, out var key)
            ? key
            : throw new FormatException($"'{code}' is not a TCMB indicative exchange-rate series code (TP.DK.CCY.A|S[.EF].YTL).");

    public override string ToString() => Code ?? "";

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    // Mirrors the CHECK constraint on fx.rate_version.series.
    [GeneratedRegex(@"^TP\.DK\.(?<ccy>[A-Z]{3})\.(?<side>[AS])(?<ef>\.EF)?\.YTL$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
