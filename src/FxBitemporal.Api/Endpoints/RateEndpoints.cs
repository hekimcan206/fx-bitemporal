using FxBitemporal.Api.Infrastructure;
using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using FxBitemporal.Core.Reports;

namespace FxBitemporal.Api.Endpoints;

internal static class RateEndpoints
{
    public const string KnownAtHeader = "X-Known-At";
    public const string ReportHashHeader = "X-Report-Sha256";

    public static RouteGroupBuilder MapRateEndpoints(this RouteGroupBuilder api)
    {
        var rates = api.MapGroup("/rates").WithTags("rates");

        rates.MapGet("/{series}", async (string series, DateOnly date, DateTimeOffset? knownAt, PostgresRateStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SeriesKey.TryParse(series, out var key))
            {
                return InvalidSeries(series);
            }

            var (version, effective) = await store.AsOfAsync(key, date, knownAt, ct);
            http.Response.Headers[KnownAtHeader] = Canonical.Timestamp(effective);
            return version is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No rate was known for that date at that known time.")
                : Results.Ok(new RateResponse(key.Code, Canonical.Date(date), Canonical.Timestamp(effective), VersionResponse.From(version)));
        })
        .WithSummary("The rate in effect on a business date, as the system knew it at knownAt (default: now)")
        .WithDescription("Pass the returned X-Known-At value back as knownAt to get the identical answer at any later time.");

        rates.MapGet("/{series}/history", async (string series, DateOnly date, PostgresRateStore store, CancellationToken ct) =>
        {
            if (!SeriesKey.TryParse(series, out var key))
            {
                return InvalidSeries(series);
            }

            var versions = await store.HistoryAsync(key, date, ct);
            return Results.Ok(new HistoryResponse(key.Code, Canonical.Date(date), [.. versions.Select(VersionResponse.From)]));
        })
        .WithSummary("Every version that ever covered a date, oldest belief first (the audit trail)");

        rates.MapGet("/{series}/changes", async (
            string series, DateOnly from, DateOnly to, DateTimeOffset knownA, DateTimeOffset? knownB, PostgresRateStore store, CancellationToken ct) =>
        {
            if (!SeriesKey.TryParse(series, out var key))
            {
                return InvalidSeries(series);
            }

            if (to < from || to.DayNumber - from.DayNumber >= TimelineDiff.MaxDays)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = [$"The range must be 1 to {TimelineDiff.MaxDays} days."] });
            }

            var (before, after, effectiveB) = await store.DiffTimelinesAsync(key, TimelineDiff.Range(from, to), knownA, knownB, ct);
            var changes = TimelineDiff.Compare(before, after, from, to);
            return Results.Ok(new ChangesResponse(
                key.Code, Canonical.Date(from), Canonical.Date(to),
                Canonical.Timestamp(Canonical.ToMicroseconds(knownA.ToUniversalTime())), Canonical.Timestamp(effectiveB),
                [.. changes.Select(c => new ChangeResponse(
                    Canonical.Date(c.Date),
                    c.Before is null ? null : VersionResponse.From(c.Before),
                    c.After is null ? null : VersionResponse.From(c.After)))]));
        })
        .WithSummary("Business dates whose answer differs between known times A and B (default B: now)");

        api.MapPost("/reports/revaluation", async (RevaluationBody body, PostgresRateStore store, RateMetrics metrics, HttpContext http, CancellationToken ct) =>
        {
            var probe = new RevaluationRequest(body.ValuationDate, body.KnownAt ?? DateTimeOffset.UnixEpoch, body.Positions ?? []);
            var errors = probe.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var series = probe.Positions.Select(p => RevaluationRequest.SeriesFor(p.Currency)).ToList();
            var (versions, effective) = await store.AsOfManyAsync(series, body.ValuationDate, body.KnownAt, ct);
            var signed = RevaluationBuilder.Build(probe with { KnownAt = effective }, versions);
            metrics.Report(body.KnownAt is not null);

            // The body IS the canonical document: anyone can recompute its SHA-256 and compare it with the header.
            http.Response.Headers[KnownAtHeader] = Canonical.Timestamp(effective);
            http.Response.Headers[ReportHashHeader] = signed.Sha256;
            return Results.Bytes(signed.CanonicalJson, "application/json");
        })
        .WithTags("reports")
        .WithSummary("Value FX positions in TRY at a valuation date, as known at knownAt (default: now)")
        .WithDescription("The response body is canonical JSON; X-Report-Sha256 is its SHA-256. The same request with the same knownAt returns the same bytes forever, even after corrections.");

        return api;
    }

    internal static IResult InvalidSeries(string series) => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["series"] = [$"'{Truncate(series)}' is not a TCMB indicative rate series code such as TP.DK.USD.A.YTL."],
    });

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "...";
}
