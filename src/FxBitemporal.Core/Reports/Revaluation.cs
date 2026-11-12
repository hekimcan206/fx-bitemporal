using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FxBitemporal.Core.Reports;

/// <summary>A foreign-currency balance to value in TRY.</summary>
public sealed record Position(string Currency, decimal Amount);

/// <summary>
/// Value <see cref="Positions"/> in TRY at <see cref="ValuationDate"/>, using the TCMB forex buying rate in effect that
/// day, <b>as the system knew it at <see cref="KnownAt"/></b>. Pinning known time is what makes the report reproducible:
/// a correction recorded later changes the answer for later known times only.
/// </summary>
public sealed partial record RevaluationRequest(DateOnly ValuationDate, DateTimeOffset KnownAt, IReadOnlyList<Position> Positions)
{
    public const int MaxPositions = 200;

    /// <summary>Returns validation errors keyed by field; empty when valid.</summary>
    public IReadOnlyDictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Positions is null || Positions.Count == 0)
        {
            errors["positions"] = ["At least one position is required."];
            return errors;
        }

        if (Positions.Count > MaxPositions)
        {
            errors["positions"] = [$"At most {MaxPositions} positions are allowed."];
        }

        var bad = Positions.Where(p => p is null || p.Currency is null || !CurrencyCode().IsMatch(p.Currency) || p.Currency == "TRY").ToList();
        if (bad.Count > 0)
        {
            errors["positions.currency"] = ["Each currency must be a three-letter upper-case ISO 4217 code other than TRY."];
        }
        else if (Positions.GroupBy(p => p.Currency, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            errors["positions.currency"] = ["Each currency may appear once; aggregate balances before valuing them."];
        }

        if (Positions.Any(p => p is not null && (p.Amount == 0 || Math.Abs(p.Amount) > 1_000_000_000_000m)))
        {
            errors["positions.amount"] = ["Amounts must be non-zero and at most 1e12 in absolute value."];
        }

        return errors;
    }

    /// <summary>The series a position is valued with: forex buying, the rate used for balance-sheet valuation.</summary>
    public static SeriesKey SeriesFor(string currency) => SeriesKey.For(currency, QuoteSide.Buying, QuoteKind.Forex);

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyCode();
}

public sealed record RevaluationLine(
    string Currency,
    decimal Amount,
    string Series,
    decimal? Rate,
    int? Unit,
    decimal? ValueTry,
    DateOnly? BulletinDate,
    long? VersionId,
    DateTimeOffset? RecordedAt,
    string Status);

public sealed record RevaluationReport(
    DateOnly ValuationDate,
    DateTimeOffset KnownAt,
    IReadOnlyList<RevaluationLine> Lines,
    decimal TotalTry,
    bool Complete);

/// <summary>A report plus its canonical bytes and their SHA-256: equal hashes mean byte-identical reports.</summary>
public sealed record SignedReport(RevaluationReport Report, byte[] CanonicalJson, string Sha256);

public static class RevaluationBuilder
{
    public const string StatusOk = "ok";
    public const string StatusNoRate = "no-rate";

    /// <param name="request">Validated request.</param>
    /// <param name="rates">The version in effect at the valuation date as known at the request's known time, per
    /// series; a missing key means no rate was known.</param>
    public static SignedReport Build(RevaluationRequest request, IReadOnlyDictionary<SeriesKey, RateVersion> rates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rates);

        var lines = new List<RevaluationLine>(request.Positions.Count);
        foreach (var position in request.Positions.OrderBy(p => p.Currency, StringComparer.Ordinal))
        {
            var series = RevaluationRequest.SeriesFor(position.Currency);
            if (!rates.TryGetValue(series, out var version))
            {
                lines.Add(new RevaluationLine(position.Currency, position.Amount, series.Code, null, null, null, null, null, null, StatusNoRate));
                continue;
            }

            // Commercial rounding to kuruş (0.01 TRY), away from zero, applied per line; the total is the sum of the
            // rounded lines so that it always equals what a reader adds up from the report.
            var valueTry = Math.Round(position.Amount * version.Value / version.Unit, 2, MidpointRounding.AwayFromZero);
            lines.Add(new RevaluationLine(
                position.Currency, position.Amount, series.Code, version.Value, version.Unit, valueTry,
                version.Fact.ObservedOn, version.Id, version.Known.From, StatusOk));
        }

        var report = new RevaluationReport(
            request.ValuationDate,
            Canonical.ToMicroseconds(request.KnownAt),
            lines,
            lines.Sum(l => l.ValueTry ?? 0m),
            lines.All(l => l.Status == StatusOk));

        var bytes = Serialize(report);
        return new SignedReport(report, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// Canonical JSON: fixed property order, decimals and instants as canonical strings, no whitespace, no volatile
    /// fields (no "generatedAt"). Version ids are included on purpose: they name the exact rows the numbers came from.
    /// </summary>
    public static byte[] Serialize(RevaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartObject();
            json.WriteString("valuationDate", Canonical.Date(report.ValuationDate));
            json.WriteString("knownAt", Canonical.Timestamp(report.KnownAt));
            json.WriteString("rateBasis", "TCMB indicative forex buying");
            json.WriteStartArray("lines");
            foreach (var line in report.Lines)
            {
                json.WriteStartObject();
                json.WriteString("currency", line.Currency);
                json.WriteString("amount", Canonical.Number(line.Amount));
                json.WriteString("series", line.Series);
                json.WriteString("status", line.Status);
                WriteOptional(json, "rate", line.Rate is { } r ? Canonical.Number(r) : null);
                if (line.Unit is { } unit)
                {
                    json.WriteNumber("unit", unit);
                }
                else
                {
                    json.WriteNull("unit");
                }

                WriteOptional(json, "valueTry", line.ValueTry is { } v ? Canonical.Number(v) : null);
                WriteOptional(json, "bulletinDate", line.BulletinDate is { } d ? Canonical.Date(d) : null);
                if (line.VersionId is { } id)
                {
                    json.WriteNumber("versionId", id);
                }
                else
                {
                    json.WriteNull("versionId");
                }

                WriteOptional(json, "recordedAt", line.RecordedAt is { } at ? Canonical.Timestamp(at) : null);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteString("totalTry", Canonical.Number(report.TotalTry));
            json.WriteBoolean("complete", report.Complete);
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
