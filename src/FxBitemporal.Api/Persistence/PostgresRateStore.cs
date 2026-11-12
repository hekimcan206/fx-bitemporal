using System.Data;
using FxBitemporal.Core;
using FxBitemporal.Core.Temporal;
using Npgsql;
using NpgsqlTypes;

namespace FxBitemporal.Api.Persistence;

/// <summary>How readers and writers agree on known time. See docs/adr/0003.</summary>
public enum KnowledgeConsistency
{
    /// <summary>
    /// Writers stamp versions after taking per-series exclusive locks; readers take the same locks shared and read the
    /// clock only once they hold them. An answer given as of known time K never changes afterwards.
    /// </summary>
    Fenced,

    /// <summary>
    /// What most implementations do: writers stamp versions with the transaction start time, readers read the clock and
    /// query without coordination. A writer that commits after a reader's query can insert a version that is "known"
    /// before the reader's known time, so a past answer can change. Kept only to measure that anomaly.
    /// </summary>
    Naive,
}

public sealed class StoreOptions
{
    public const string Section = "Store";

    public KnowledgeConsistency Consistency { get; set; } = KnowledgeConsistency.Fenced;
}

/// <summary>The result of one write transaction.</summary>
public sealed record WriteResult(DateTimeOffset? KnownFrom, int Observations, int Closed, int Inserted, int NoOpSeries)
{
    public bool Changed => Closed + Inserted > 0;
}

/// <summary>Known time asked for is later than the database clock: such an answer could still change.</summary>
public sealed class FutureKnownTimeException(DateTimeOffset requested, DateTimeOffset horizon)
    : Exception($"knownAt {Canonical.Timestamp(requested)} is after the current knowledge horizon {Canonical.Timestamp(horizon)}.")
{
    public DateTimeOffset Requested { get; } = requested;

    public DateTimeOffset Horizon { get; } = horizon;
}

/// <summary>
/// The bitemporal store on PostgreSQL 17. All timestamps come from the database clock; the exclusion constraint, the
/// append-only trigger and column-level grants enforce the model even against a buggy caller.
/// </summary>
public sealed class PostgresRateStore(NpgsqlDataSource dataSource, StoreOptions options)
{
    private const string Columns =
        "id, series, lower(valid_during), upper(valid_during), lower(known_during), upper(known_during), value, unit, observed_on, source, reason";

    public KnowledgeConsistency Consistency => options.Consistency;

    /// <summary>
    /// Records observations (typically one bulletin) in one transaction: every resulting version becomes known at the
    /// same instant. Re-recording what is already believed changes nothing.
    /// </summary>
    public Task<WriteResult> RecordAsync(IReadOnlyList<Observation> observations, string recordedBy, CancellationToken cancellationToken = default) =>
        RecordAsync(observations, recordedBy, beforeCommit: null, cancellationToken);

    /// <param name="beforeCommit">Test hook: runs after the writes, before COMMIT, to widen the commit window on purpose.</param>
    internal async Task<WriteResult> RecordAsync(
        IReadOnlyList<Observation> observations, string recordedBy, Func<Task>? beforeCommit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return new WriteResult(null, 0, 0, 0, 0);
        }

        var bySeries = observations.GroupBy(o => o.Series).ToList();
        var seriesCodes = bySeries.Select(g => g.Key.Code).ToArray();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Lock first, then read the clock: the instant is taken while no reader of these series holds the fence, so any
        // reader whose knowledge horizon is later than this instant must wait for our commit and will see our rows.
        await using (var lockCommand = new NpgsqlCommand("SELECT fx.lock_series($1, false)", connection, tx))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = seriesCodes });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Strictly later than every currently believed version of these series, even if the clock stepped back: a
        // version must never be closed before it was opened.
        var clock = options.Consistency == KnowledgeConsistency.Fenced ? "clock_timestamp()" : "now()";
        DateTimeOffset knownFrom;
        await using (var clockCommand = new NpgsqlCommand(
            $"""
            SELECT greatest({clock},
                   (SELECT max(lower(known_during)) FROM fx.rate_version
                     WHERE series = ANY($1) AND upper_inf(known_during)) + interval '1 microsecond')
            """, connection, tx))
        {
            clockCommand.Parameters.Add(new NpgsqlParameter { Value = seriesCodes });
            knownFrom = ToOffset((DateTime)(await clockCommand.ExecuteScalarAsync(cancellationToken))!);
        }

        var current = await LoadCurrentAsync(connection, tx, bySeries, cancellationToken);

        int closed = 0, inserted = 0, noOp = 0;
        var close = new List<long>();
        var insertRows = new List<(SeriesKey Series, PlannedVersion Version)>();
        foreach (var group in bySeries)
        {
            var plan = BatchPlanner.Plan(current.GetValueOrDefault(group.Key, []), group.OrderBy(o => o.Date));
            if (plan.IsNoOp)
            {
                noOp++;
                continue;
            }

            close.AddRange(plan.Close);
            insertRows.AddRange(plan.Insert.Select(p => (group.Key, p)));
        }

        if (close.Count > 0)
        {
            await using var closeCommand = new NpgsqlCommand(
                "UPDATE fx.rate_version SET known_during = tstzrange(lower(known_during), $2, '[)') WHERE id = ANY($1) AND upper_inf(known_during)",
                connection, tx);
            closeCommand.Parameters.Add(new NpgsqlParameter { Value = close.ToArray() });
            closeCommand.Parameters.Add(new NpgsqlParameter { Value = knownFrom.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            closed = await closeCommand.ExecuteNonQueryAsync(cancellationToken);
            if (closed != close.Count)
            {
                // Under the fence this cannot happen; if it does, something wrote without the lock. Fail loudly.
                throw new InvalidOperationException($"Expected to close {close.Count} versions but closed {closed}.");
            }
        }

        if (insertRows.Count > 0)
        {
            inserted = await InsertAsync(connection, tx, insertRows, knownFrom, recordedBy, cancellationToken);
        }

        if (beforeCommit is not null)
        {
            await beforeCommit();
        }

        await tx.CommitAsync(cancellationToken);
        return new WriteResult(closed + inserted > 0 ? knownFrom : null, observations.Count, closed, inserted, noOp);
    }

    /// <summary>"Which rate applied to <paramref name="date"/>, as known at <paramref name="knownAt"/> (default: now)?"</summary>
    public Task<(RateVersion? Version, DateTimeOffset KnownAt)> AsOfAsync(
        SeriesKey series, DateOnly date, DateTimeOffset? knownAt, CancellationToken cancellationToken = default) =>
        ReadAsync([series], knownAt, async (connection, tx, effective) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT {Columns} FROM fx.rate_version WHERE series = $1 AND valid_during @> $2 AND known_during @> $3",
                connection, tx);
            command.Parameters.Add(new NpgsqlParameter { Value = series.Code });
            command.Parameters.Add(new NpgsqlParameter { Value = date });
            command.Parameters.Add(new NpgsqlParameter { Value = effective.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            var rows = await ReadVersionsAsync(command, cancellationToken);
            return rows.Count == 0 ? null : rows[0];
        }, cancellationToken);

    /// <summary>The as-of versions for several series at once (one report), under one fence and one known time.</summary>
    public Task<(Dictionary<SeriesKey, RateVersion> Versions, DateTimeOffset KnownAt)> AsOfManyAsync(
        IReadOnlyCollection<SeriesKey> series, DateOnly date, DateTimeOffset? knownAt, CancellationToken cancellationToken = default) =>
        ReadAsync(series, knownAt, async (connection, tx, effective) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT {Columns} FROM fx.rate_version WHERE series = ANY($1) AND valid_during @> $2 AND known_during @> $3",
                connection, tx);
            command.Parameters.Add(new NpgsqlParameter { Value = series.Select(s => s.Code).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = date });
            command.Parameters.Add(new NpgsqlParameter { Value = effective.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            return (await ReadVersionsAsync(command, cancellationToken)).ToDictionary(v => v.Series);
        }, cancellationToken);

    /// <summary>
    /// The valid-time timelines of one series as known at <paramref name="knownA"/> and at <paramref name="knownB"/>
    /// (default: the knowledge horizon), read under one fence so both are reproducible.
    /// </summary>
    public async Task<(IReadOnlyList<RateVersion> Before, IReadOnlyList<RateVersion> After, DateTimeOffset KnownB)> DiffTimelinesAsync(
        SeriesKey series, ValidPeriod range, DateTimeOffset knownA, DateTimeOffset? knownB, CancellationToken cancellationToken = default)
    {
        var pinnedA = Canonical.ToMicroseconds(knownA.ToUniversalTime());
        var pinnedB = knownB is { } b ? Canonical.ToMicroseconds(b.ToUniversalTime()) : (DateTimeOffset?)null;

        // The fence validates the later of the two pinned instants (or takes the horizon when B is "now").
        DateTimeOffset? fenceAt = pinnedB is { } pb ? (pb > pinnedA ? pb : pinnedA) : null;
        var ((before, after, effectiveB), _) = await ReadAsync([series], fenceAt, async (connection, tx, effective) =>
        {
            if (pinnedA > effective)
            {
                throw new FutureKnownTimeException(pinnedA, effective);
            }

            var bAt = pinnedB ?? effective;
            return (await TimelineAsync(connection, tx, series, range, pinnedA, cancellationToken),
                    await TimelineAsync(connection, tx, series, range, bAt, cancellationToken),
                    bAt);
        }, cancellationToken);

        return (before, after, effectiveB);
    }

    private static async Task<IReadOnlyList<RateVersion>> TimelineAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SeriesKey series, ValidPeriod range, DateTimeOffset knownAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT {Columns} FROM fx.rate_version
             WHERE series = $1 AND valid_during && daterange($2, $3, '[)') AND known_during @> $4
             ORDER BY lower(valid_during)
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = series.Code });
        command.Parameters.Add(new NpgsqlParameter { Value = range.From });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)range.To ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        command.Parameters.Add(new NpgsqlParameter { Value = knownAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await ReadVersionsAsync(command, cancellationToken);
    }

    /// <summary>Every version that ever covered <paramref name="date"/>, in known-time order: the audit trail.</summary>
    public async Task<IReadOnlyList<RateVersion>> HistoryAsync(SeriesKey series, DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM fx.rate_version WHERE series = $1 AND valid_during @> $2 ORDER BY lower(known_during), id");
        command.Parameters.Add(new NpgsqlParameter { Value = series.Code });
        command.Parameters.Add(new NpgsqlParameter { Value = date });
        return await ReadVersionsAsync(command, cancellationToken);
    }

    /// <summary>Provenance of a version (who recorded it); kept out of <see cref="RateVersion"/> to keep the model pure.</summary>
    public async Task<string?> RecordedByAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT recorded_by FROM fx.rate_version WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Runs a read under the knowledge fence. The effective known time is the requested one, or the database clock read
    /// after the shared locks are held; a requested known time later than that clock is refused.
    /// </summary>
    private async Task<(T Result, DateTimeOffset KnownAt)> ReadAsync<T>(
        IReadOnlyCollection<SeriesKey> series,
        DateTimeOffset? knownAt,
        Func<NpgsqlConnection, NpgsqlTransaction, DateTimeOffset, Task<T>> body,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // One round trip; the statements of a batch run in order, so the clock is read after the locks are held.
        DateTimeOffset horizon;
        await using (var batch = new NpgsqlBatch(connection, tx))
        {
            if (options.Consistency == KnowledgeConsistency.Fenced)
            {
                var lockCommand = new NpgsqlBatchCommand("SELECT fx.lock_series($1, true)");
                lockCommand.Parameters.Add(new NpgsqlParameter { Value = series.Select(s => s.Code).ToArray() });
                batch.BatchCommands.Add(lockCommand);
            }

            batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT clock_timestamp()"));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            if (options.Consistency == KnowledgeConsistency.Fenced)
            {
                await reader.NextResultAsync(cancellationToken);
            }

            await reader.ReadAsync(cancellationToken);
            horizon = ToOffset(reader.GetDateTime(0));
        }

        var requested = knownAt is { } k ? Canonical.ToMicroseconds(k.ToUniversalTime()) : (DateTimeOffset?)null;
        if (requested > horizon)
        {
            throw new FutureKnownTimeException(requested.Value, horizon);
        }

        var effective = requested ?? horizon;
        var result = await body(connection, tx, effective);
        await tx.CommitAsync(cancellationToken);
        return (result, effective);
    }

    private static async Task<Dictionary<SeriesKey, List<RateVersion>>> LoadCurrentAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, List<IGrouping<SeriesKey, Observation>> bySeries, CancellationToken cancellationToken)
    {
        // One round trip for all series: the believed versions overlapping the observed dates, plus the first believed
        // version starting after the latest observed date (it bounds the last valid period).
        await using var batch = new NpgsqlBatch(connection, tx);
        foreach (var group in bySeries)
        {
            var min = group.Min(o => o.Date);
            var endExclusive = group.Max(o => o.Date).AddDays(1);
            var command = new NpgsqlBatchCommand(
                $"""
                SELECT {Columns} FROM fx.rate_version
                 WHERE series = $1 AND upper_inf(known_during) AND valid_during && daterange($2, $3, '[)')
                UNION ALL
                (SELECT {Columns} FROM fx.rate_version
                  WHERE series = $1 AND upper_inf(known_during) AND lower(valid_during) >= $3
                  ORDER BY lower(valid_during) LIMIT 1)
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = group.Key.Code });
            command.Parameters.Add(new NpgsqlParameter { Value = min });
            command.Parameters.Add(new NpgsqlParameter { Value = endExclusive });
            batch.BatchCommands.Add(command);
        }

        var result = new Dictionary<SeriesKey, List<RateVersion>>();
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var version = Map(reader);
                if (!result.TryGetValue(version.Series, out var list))
                {
                    result[version.Series] = list = [];
                }

                list.Add(version);
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        return result;
    }

    private static async Task<int> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, List<(SeriesKey Series, PlannedVersion Version)> rows,
        DateTimeOffset knownFrom, string recordedBy, CancellationToken cancellationToken)
    {
        // One statement for the whole batch: arrays in, unnest, set-based insert.
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fx.rate_version (series, valid_during, known_during, value, unit, observed_on, source, reason, recorded_by)
            SELECT s, daterange(vf, vt, '[)'), tstzrange($9, NULL, '[)'), v, u, o, src, r, $10
              FROM unnest($1::text[], $2::date[], $3::date[], $4::numeric[], $5::int[], $6::date[], $7::text[], $8::text[])
                   AS t(s, vf, vt, v, u, o, src, r)
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Series.Code).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Valid.From).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Valid.To).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Fact.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Fact.Unit).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Fact.ObservedOn).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Fact.Source).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Version.Fact.Reason).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = knownFrom.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = recordedBy });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<RateVersion>> ReadVersionsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<RateVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    private static RateVersion Map(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        SeriesKey.Parse(reader.GetString(1)),
        new ValidPeriod(reader.GetFieldValue<DateOnly>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3)),
        new KnownPeriod(ToOffset(reader.GetDateTime(4)), reader.IsDBNull(5) ? null : ToOffset(reader.GetDateTime(5))),
        new RateFact(
            reader.GetDecimal(6),
            reader.GetInt32(7),
            reader.GetFieldValue<DateOnly>(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)));

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
