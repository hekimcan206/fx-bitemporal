using System.Security.Cryptography;
using System.Text;
using FxBitemporal.Api.Infrastructure;
using FxBitemporal.Api.Ingestion;
using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using FxBitemporal.Sources.Tcmb;

namespace FxBitemporal.Api.Endpoints;

public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Shared secret for write endpoints; when empty, the write endpoints are disabled.</summary>
    public string? Key { get; set; }
}

/// <summary>
/// The only two ways belief changes: ingesting bulletins from a source, and recording a correction with a reason.
/// Both append versions; neither can edit or delete one (the database role cannot either).
/// </summary>
internal static class AdminEndpoints
{
    public const string AdminKeyHeader = "X-Admin-Key";

    /// <summary>Turkey has used UTC+3 all year since 2016; a fixed offset avoids depending on tzdata in the image.</summary>
    private static readonly TimeSpan Istanbul = TimeSpan.FromHours(3);

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("admin")
            .AddEndpointFilter(async (context, next) =>
            {
                var configured = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminOptions>>().Value.Key;
                return IsAdmin(configured, context.HttpContext.Request.Headers[AdminKeyHeader])
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Admin key required.");
            });

        admin.MapPost("/ingest", async (IngestRequest request, IngestionService ingestion, CancellationToken ct) =>
        {
            if (request.To < request.From || request.To.DayNumber - request.From.DayNumber >= TcmbArchiveSource.MaxDays)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = [$"The range must be 1 to {TcmbArchiveSource.MaxDays} days."] });
            }

            try
            {
                var actor = $"ingest:{request.Source ?? "default"}";
                return Results.Ok(await ingestion.RunAsync(request.Source, request.From, request.To, actor, ct));
            }
            catch (ArgumentException ex) when (ex is not ArgumentOutOfRangeException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = [ex.Message] });
            }
        }).WithSummary("Ingest bulletins for a date range from a source (fixture, tcmb-archive, evds); idempotent");

        admin.MapPost("/corrections", async (CorrectionRequest request, PostgresRateStore store, RateMetrics metrics, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!SeriesKey.TryParse(request.Series, out var series))
            {
                errors["series"] = ["Not a TCMB indicative rate series code such as TP.DK.USD.A.YTL."];
            }

            if (request.Value is <= 0 or >= 1_000_000_000_000m)
            {
                errors["value"] = ["The rate must be positive and below 1e12."];
            }

            if (request.Unit is <= 0 or > 10_000)
            {
                errors["unit"] = ["The unit must be between 1 and 10000."];
            }

            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 500)
            {
                errors["reason"] = ["A correction needs a reason of 10 to 500 characters: it is part of the audit trail."];
            }

            if (request.BulletinDate > DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Istanbul).DateTime))
            {
                errors["bulletinDate"] = ["A bulletin date cannot be in the future."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // Default to the unit the series is quoted in (JPY per 100), taken from the version being corrected.
            var unit = request.Unit ?? (await store.AsOfAsync(series, request.BulletinDate, null, ct)).Version?.Unit ?? 1;
            var observation = new Observation(series, request.BulletinDate, request.Value, unit, RateSources.Correction, request.Reason.Trim());
            var result = await store.RecordAsync([observation], "admin:correction", ct);
            if (result.Changed)
            {
                metrics.Correction();
            }

            metrics.Recorded(RateSources.Correction, 1, result.Inserted, result.Closed);
            return Results.Ok(new CorrectionResponse(
                series.Code, Canonical.Date(request.BulletinDate),
                result.KnownFrom is { } k ? Canonical.Timestamp(k) : null,
                result.Closed, result.Inserted, result.Changed));
        }).WithSummary("Correct the published value of one bulletin; answers as of earlier known times are unchanged");

        return api;
    }

    internal static bool IsAdmin(string? configured, string? presented)
    {
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // Constant-time comparison of fixed-length digests: no timing signal about the key's content or length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)), SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }
}
