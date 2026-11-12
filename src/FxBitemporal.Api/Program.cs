using System.Diagnostics;
using FxBitemporal.Api.Endpoints;
using FxBitemporal.Api.Infrastructure;
using FxBitemporal.Api.Ingestion;
using FxBitemporal.Api.Persistence;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;


var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.Configure<SourceOptions>(builder.Configuration.GetSection(SourceOptions.Section));
builder.Services.AddSingleton<IngestionService>();

// Upstream TCMB endpoints get timeouts, retries with jitter and a circuit breaker; a bulletin fetch is idempotent.
builder.Services.AddHttpClient(IngestionService.ArchiveClient, (sp, http) =>
        http.BaseAddress = sp.GetRequiredService<IOptions<SourceOptions>>().Value.TcmbArchiveBaseAddress)
    .AddStandardResilienceHandler();
builder.Services.AddHttpClient(IngestionService.EvdsClient, (sp, http) =>
        http.BaseAddress = sp.GetRequiredService<IOptions<SourceOptions>>().Value.EvdsBaseAddress)
    .AddStandardResilienceHandler();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "fx-bitemporal";
    document.Info.Version = "v1";
    document.Info.Description = "TCMB exchange rates with a bitemporal model: the rate valid on a date, as the system knew it at a known time.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, several roles: `--migrate-only` applies migrations as the schema owner; `--ingest-fixtures` loads the
// recorded bulletins as the application role; with no flag the service serves requests.
if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return 0;
}

if (args.Contains("--ingest-fixtures"))
{
    var summary = await app.Services.GetRequiredService<IngestionService>().RunAsync(
        FxBitemporal.Core.RateSources.Fixture,
        FxBitemporal.Sources.Fixtures.RecordedBulletinSource.Dates[0], FxBitemporal.Sources.Fixtures.RecordedBulletinSource.Dates[^1],
        "ingest:fixture", CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary, System.Text.Json.JsonSerializerOptions.Web));
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("fx-bitemporal"));
}

app.MapHealthEndpoints();
app.MapGroup("/api/v1")
    .WithGroupName("v1")
    .MapRateEndpoints()
    .MapAdminEndpoints();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();
return 0;

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
