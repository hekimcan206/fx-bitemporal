using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FxBitemporal.Api.Persistence;

internal static class PersistenceExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.Configure<StoreOptions>(configuration.GetSection(StoreOptions.Section));
        services.AddSingleton(_ => CreateDataSource(connectionString));
        services.AddSingleton(sp => new PostgresRateStore(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IOptions<StoreOptions>>().Value));
        services.AddSingleton<MigrationRunner>();
        return services;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // Writers and fenced readers wait on per-series advisory locks held for one short transaction. lock_timeout
        // bounds that wait (the request then fails with a retryable 503); the idle timeout stops a stalled client from
        // holding a fence forever.
        builder.ConnectionStringBuilder.Options ??= "-c lock_timeout=5000 -c idle_in_transaction_session_timeout=30000";

        // No Kerberos here; skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.Build();
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(cancellationToken);
}

/// <summary>Readiness: can we open a connection and run a trivial query within the probe timeout?</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.", ex);
        }
    }
}
