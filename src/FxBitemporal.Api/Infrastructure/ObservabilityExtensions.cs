using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace FxBitemporal.Api.Infrastructure;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Structured JSON logs on stdout plus OpenTelemetry traces and metrics. The OTLP exporter is enabled only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set, so the service runs without a collector.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        // Configured through DI rather than a static bootstrap logger, so several hosts can live in one process
        // (WebApplicationFactory instances in integration tests).
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
        var serviceVersion = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString(3);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                .AddMeter(RateMetrics.MeterName));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        builder.Services.AddSingleton<RateMetrics>();
        return builder;
    }
}

/// <summary>Domain metrics: how much belief changes, and how often a caller asks about known time.</summary>
internal sealed class RateMetrics : IDisposable
{
    public const string MeterName = "FxBitemporal.Api";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _observations;
    private readonly Counter<long> _versionsInserted;
    private readonly Counter<long> _versionsClosed;
    private readonly Counter<long> _corrections;
    private readonly Counter<long> _reports;

    public RateMetrics()
    {
        _observations = _meter.CreateCounter<long>("fx.observations", description: "Observations offered to the store, by source.");
        _versionsInserted = _meter.CreateCounter<long>("fx.versions.inserted", description: "Versions that started being believed.");
        _versionsClosed = _meter.CreateCounter<long>("fx.versions.closed", description: "Versions that stopped being believed.");
        _corrections = _meter.CreateCounter<long>("fx.corrections", description: "Corrections recorded.");
        _reports = _meter.CreateCounter<long>("fx.reports", description: "Revaluation reports built, by whether known time was pinned.");
    }

    public void Recorded(string source, int observations, int inserted, int closed)
    {
        _observations.Add(observations, new KeyValuePair<string, object?>("source", source));
        _versionsInserted.Add(inserted);
        _versionsClosed.Add(closed);
    }

    public void Correction() => _corrections.Add(1);

    public void Report(bool pinned) => _reports.Add(1, new KeyValuePair<string, object?>("pinned", pinned));

    public void Dispose() => _meter.Dispose();
}
