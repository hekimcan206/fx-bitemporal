using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using FxBitemporal.Sources.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FxBitemporal.IntegrationTests.Infrastructure.FxEnvironment))]

namespace FxBitemporal.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL 17, initialised with the same role script docker compose uses and migrated as <c>fx_migrator</c>; the
/// service runs in-process connecting as <c>fx_app</c>. All 28 recorded TCMB bulletins are ingested once, and
/// <see cref="FixturesKnownAt"/> pins the known time right after that, so tests that check published values are immune
/// to corrections other tests record later.
/// </summary>
public sealed class FxEnvironment : IAsyncLifetime
{
    public const string AdminKey = "admin-test-only";
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fxbitemporal")
        .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
            "/docker-entrypoint-initdb.d/01-roles.sh")
        .Build();

    private ApiFactory? _api;

    public string SuperuserConnectionString => _postgres.GetConnectionString();

    public string MigratorConnectionString => WithCredentials("fx_migrator", MigratorPassword);

    public string AppConnectionString => WithCredentials("fx_app", AppPassword);

    public NpgsqlDataSource AppDataSource { get; private set; } = null!;

    public PostgresRateStore Store { get; private set; } = null!;

    public DateTimeOffset FixturesKnownAt { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var migrator = PersistenceExtensions.CreateDataSource(MigratorConnectionString))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        AppDataSource = PersistenceExtensions.CreateDataSource(AppConnectionString);
        Store = CreateStore(KnowledgeConsistency.Fenced);

        foreach (var date in RecordedBulletinSource.Dates)
        {
            await Store.RecordAsync([.. RecordedBulletinSource.Load(date).ToObservations(RateSources.Fixture)], "test:fixtures");
        }

        FixturesKnownAt = (await Store.AsOfAsync(SeriesKey.Parse("TP.DK.USD.A.YTL"), new DateOnly(2026, 10, 30), null)).KnownAt;

        _api = new ApiFactory(AppConnectionString);
        using var warmup = Client();
        (await warmup.GetAsync(new Uri("health/ready", UriKind.Relative))).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (AppDataSource is not null)
        {
            await AppDataSource.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public PostgresRateStore CreateStore(KnowledgeConsistency consistency) =>
        new(AppDataSource, new StoreOptions { Consistency = consistency });

    public HttpClient Client() => _api!.CreateClient();

    public HttpClient Admin()
    {
        var client = _api!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return client;
    }

    private string WithCredentials(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = username,
            Password = password,
            MaxPoolSize = 50,
            // Generous connect timeout: the host is shared with other projects' containers.
            Timeout = 60,
        }.ConnectionString;

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Admin:Key", AdminKey);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }
}
