using FxBitemporal.Api.Persistence;
using FxBitemporal.Core;
using FxBitemporal.Core.Temporal;
using FxBitemporal.IntegrationTests.Infrastructure;
using FxBitemporal.Sources.Fixtures;
using Npgsql;

namespace FxBitemporal.IntegrationTests;

/// <summary>The PostgreSQL store against the model, and the database-level guarantees, on a real PostgreSQL 17.</summary>
public sealed class StoreTests(FxEnvironment env)
{
    private static readonly SeriesKey Usd = SeriesKey.Parse("TP.DK.USD.A.YTL");

    [Fact]
    public async Task Recorded_bulletins_answer_with_published_values_and_carry_over_weekends()
    {
        var friday = await env.Store.AsOfAsync(Usd, new DateOnly(2026, 10, 30), env.FixturesKnownAt);
        var sunday = await env.Store.AsOfAsync(Usd, new DateOnly(2026, 11, 1), env.FixturesKnownAt);
        var holiday = await env.Store.AsOfAsync(Usd, new DateOnly(2026, 10, 29), env.FixturesKnownAt); // Republic Day

        friday.Version!.Value.Should().Be(49.7747m);
        sunday.Version!.Id.Should().Be(friday.Version.Id, "the weekend is covered by Friday's version");
        holiday.Version!.Fact.ObservedOn.Should().Be(new DateOnly(2026, 10, 27), "no bulletin on 28-29 October");
        sunday.KnownAt.Should().Be(env.FixturesKnownAt);
    }

    [Fact]
    public async Task Re_ingesting_every_recorded_bulletin_changes_nothing()
    {
        foreach (var date in RecordedBulletinSource.Dates)
        {
            var result = await env.Store.RecordAsync([.. RecordedBulletinSource.Load(date).ToObservations(RateSources.TcmbArchive)], "test:reingest");
            result.Changed.Should().BeFalse($"bulletin {date} is already believed");
            result.KnownFrom.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_correction_is_visible_later_and_invisible_as_of_earlier_known_times()
    {
        var series = SeriesKey.Parse("TP.DK.QAA.A.YTL"); // synthetic currency: this test owns the series
        await env.Store.RecordAsync([Obs(series, 2, 30.0m), Obs(series, 5, 31.0m)], "test");
        var (_, before) = await env.Store.AsOfAsync(series, D(3), null);

        var correction = await env.Store.RecordAsync([Obs(series, 2, 30.5m, RateSources.Correction, "synthetic correction")], "test");

        correction.Closed.Should().Be(1);
        correction.Inserted.Should().Be(1);
        (await env.Store.AsOfAsync(series, D(3), before)).Version!.Value.Should().Be(30.0m);
        (await env.Store.AsOfAsync(series, D(3), null)).Version!.Value.Should().Be(30.5m);
        (await env.Store.AsOfAsync(series, D(6), null)).Version!.Value.Should().Be(31.0m);
        // Both bulletins were written in one transaction, so only the net effect exists: one fixture version for day 3.
        var history = await env.Store.HistoryAsync(series, D(3));
        history.Select(v => (v.Value, v.Known.IsCurrent, v.Fact.Source)).Should().Equal(
            (30.0m, false, RateSources.Fixture), (30.5m, true, RateSources.Correction));
        history[1].Fact.Reason.Should().Be("synthetic correction");
        (await env.Store.RecordedByAsync(history[1].Id)).Should().Be("test");
    }

    [Fact]
    public async Task A_known_time_after_the_horizon_is_refused()
    {
        var act = () => env.Store.AsOfAsync(Usd, new DateOnly(2026, 10, 30), DateTimeOffset.UtcNow.AddMinutes(5));

        await act.Should().ThrowAsync<FutureKnownTimeException>();
    }

    /// <summary>
    /// Random histories (bulletins, back-fills, re-ingestions, corrections; several observations per transaction) run
    /// through PostgreSQL and through the in-memory model with PostgreSQL's own known-time instants. After every step,
    /// every (date, known time) answer seen so far must agree.
    /// </summary>
    [Fact]
    public async Task The_PostgreSQL_store_agrees_with_the_model_on_random_histories()
    {
        var random = new Random(20261112);
        for (var history = 0; history < 12; history++)
        {
            var series = SeriesKey.For($"Q{(char)('B' + history)}X", QuoteSide.Buying, QuoteKind.Forex);
            var model = new InMemoryRateStore();
            var instants = new List<DateTimeOffset>();
            for (var step = 0; step < 25; step++)
            {
                var batch = Enumerable.Range(0, random.Next(1, 4))
                    .Select(_ => Obs(series, random.Next(1, 30), 40m + random.Next(0, 4) * 0.25m))
                    .ToList();
                var result = await env.Store.RecordAsync(batch, "test:model");
                if (result.KnownFrom is { } knownFrom)
                {
                    model.RecordBatch(batch, knownFrom);
                    instants.Add(knownFrom);
                }
            }

            foreach (var knownAt in instants)
            {
                for (var day = 0; day <= 31; day++)
                {
                    var date = new DateOnly(2026, 12, 1).AddDays(day - 1);
                    var expected = model.AsOf(series, date, knownAt);
                    var actual = (await env.Store.AsOfAsync(series, date, knownAt)).Version;
                    (actual?.Fact, actual?.Valid).Should().Be((expected?.Fact, expected?.Valid), $"history {history}, {date} as of {knownAt:O}");
                }
            }
        }
    }

    [Fact]
    public async Task The_exclusion_constraint_rejects_a_bitemporal_overlap_even_from_the_owner()
    {
        await using var connection = new NpgsqlConnection(env.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fx.rate_version (series, valid_during, known_during, value, unit, observed_on, source, recorded_by)
            VALUES ('TP.DK.USD.A.YTL', daterange('2026-10-30', '2026-10-31'), tstzrange(now() - interval '1 day', NULL), 1, 1, '2026-10-30', 'fixture', 'test')
            """, connection);

        var act = () => command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ExclusionViolation);
    }

    [Theory]
    [InlineData("DELETE FROM fx.rate_version WHERE series = 'TP.DK.USD.A.YTL'")]
    [InlineData("UPDATE fx.rate_version SET value = 1 WHERE series = 'TP.DK.USD.A.YTL'")]
    [InlineData("TRUNCATE fx.rate_version")]
    public async Task The_application_role_cannot_edit_or_delete_history(string sql)
    {
        await using var command = env.AppDataSource.CreateCommand(sql);

        var act = () => command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("DELETE FROM fx.rate_version WHERE series = 'TP.DK.USD.A.YTL'")]
    [InlineData("UPDATE fx.rate_version SET value = value + 1 WHERE series = 'TP.DK.USD.A.YTL' AND upper_inf(known_during)")]
    [InlineData("UPDATE fx.rate_version SET known_during = tstzrange(lower(known_during), NULL) WHERE NOT upper_inf(known_during)")]
    [InlineData("UPDATE fx.rate_version SET valid_during = daterange('2020-01-01', NULL) WHERE upper_inf(known_during)")]
    [InlineData("TRUNCATE fx.rate_version")]
    public async Task The_append_only_trigger_stops_even_the_table_owner(string sql)
    {
        await using var connection = new NpgsqlConnection(env.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var act = () => command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task A_correction_without_a_reason_is_rejected_by_the_schema()
    {
        var act = () => env.Store.RecordAsync([Obs(SeriesKey.Parse("TP.DK.QAB.A.YTL"), 2, 1m, RateSources.Correction)], "test");

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("correction_has_reason");
    }

    private static DateOnly D(int day) => new(2026, 12, day);

    private static Observation Obs(SeriesKey series, int day, decimal value, string source = RateSources.Fixture, string? reason = null) =>
        new(series, D(day), value, 1, source, reason);
}
