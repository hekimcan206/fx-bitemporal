using FxBitemporal.Api.Infrastructure;
using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using FxBitemporal.Sources;
using FxBitemporal.Sources.Evds;
using FxBitemporal.Sources.Fixtures;
using FxBitemporal.Sources.Tcmb;

namespace FxBitemporal.Api.Ingestion;

public sealed class SourceOptions
{
    public const string Section = "Sources";

    /// <summary>fixture (default, offline), tcmb-archive (public, no key) or evds (needs an API key).</summary>
    public string Default { get; set; } = RateSources.Fixture;

    public Uri TcmbArchiveBaseAddress { get; set; } = TcmbArchiveSource.DefaultBaseAddress;

    public Uri EvdsBaseAddress { get; set; } = EvdsSource.DefaultBaseAddress;

    /// <summary>Never logged, never returned. Supplied through the environment (Sources__EvdsApiKey).</summary>
    public string? EvdsApiKey { get; set; }

    public string[] EvdsSeries { get; set; } = ["TP.DK.USD.A.YTL", "TP.DK.USD.S.YTL", "TP.DK.EUR.A.YTL", "TP.DK.EUR.S.YTL", "TP.DK.GBP.A.YTL", "TP.DK.JPY.A.YTL"];
}

public sealed record IngestionSummary(string Source, int Bulletins, int Observations, int ChangedBulletins, int Inserted, int Closed);

/// <summary>
/// Pulls a date range from a source and records it one bulletin per transaction, oldest first. Every version from one
/// bulletin therefore becomes known at one instant, and a failure part-way leaves whole bulletins, never half of one.
/// </summary>
internal sealed partial class IngestionService(
    IHttpClientFactory httpClients,
    Microsoft.Extensions.Options.IOptions<SourceOptions> options,
    PostgresRateStore store,
    RateMetrics metrics,
    ILogger<IngestionService> logger)
{
    public const string ArchiveClient = "tcmb-archive";
    public const string EvdsClient = "evds";

    public IRateSource Resolve(string? name) => (name ?? options.Value.Default) switch
    {
        RateSources.Fixture => new RecordedBulletinSource(),
        RateSources.TcmbArchive => new TcmbArchiveSource(httpClients.CreateClient(ArchiveClient)),
        RateSources.Evds => new EvdsSource(
            httpClients.CreateClient(EvdsClient),
            options.Value.EvdsApiKey ?? "",
            [.. options.Value.EvdsSeries.Select(SeriesKey.Parse)]),
        var other => throw new ArgumentException($"Unknown source '{other}'. Use fixture, tcmb-archive or evds.", nameof(name)),
    };

    public async Task<IngestionSummary> RunAsync(string? sourceName, DateOnly from, DateOnly toInclusive, string actor, CancellationToken cancellationToken)
    {
        var source = Resolve(sourceName);
        var observations = new List<Observation>();
        await foreach (var observation in source.FetchAsync(from, toInclusive, cancellationToken))
        {
            observations.Add(observation);
        }

        int bulletins = 0, changed = 0, inserted = 0, closed = 0;
        foreach (var bulletin in observations.GroupBy(o => o.Date).OrderBy(g => g.Key))
        {
            var result = await store.RecordAsync([.. bulletin], actor, cancellationToken);
            bulletins++;
            changed += result.Changed ? 1 : 0;
            inserted += result.Inserted;
            closed += result.Closed;
            metrics.Recorded(source.Name, result.Observations, result.Inserted, result.Closed);
        }

        LogIngested(source.Name, from, toInclusive, bulletins, observations.Count, changed);
        return new IngestionSummary(source.Name, bulletins, observations.Count, changed, inserted, closed);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ingested {Source} {From}..{To}: {Bulletins} bulletins, {Observations} observations, {Changed} bulletins changed belief")]
    private partial void LogIngested(string source, DateOnly from, DateOnly to, int bulletins, int observations, int changed);
}
