# 0001: Bitemporal versions on PostgreSQL ranges, with the invariant enforced by the database

- Status: accepted
- Date: 2026-11-12

## Context

Finance asks two different questions about an exchange rate, and most rate tables can only answer one of them:

1. **"Which rate applied on 31 October?"** That is *valid time*: when a fact is true in the real world. TCMB (the
   Central Bank of the Republic of Türkiye) publishes indicative rates on business days only, and the latest bulletin
   applies until the next one, so a rate is valid for a range of dates, not for one day.
2. **"Which rate did our system use when it produced the month-end report on 3 November?"** That is *known time*
   (transaction time): when the system believed a fact. Rates get corrected after the fact. A missed bulletin is
   back-filled, or a published value turns out to be wrong. Once that happens, a report re-run with today's data no
   longer matches the one that was filed, and nobody can explain the difference.

A table that overwrites rates answers (1) with today's knowledge only. A table that appends "rate + loaded_at" rows
answers (2), but every reader has to rebuild the valid-time picture, and nothing stops two rows from claiming the same
day at the same time. The terms valid time and transaction time come from Snodgrass and colleagues, and SQL:2011 uses
them too. Fowler calls them "actual" and "record" history and notes that record history is append-only.

## Options considered

1. **Overwrite in place plus an audit-log table.** The usual approach, and simple to read. But the audit log holds
   row images, not answers. Reproducing "the report as of 3 November" means replaying the log, and that only works if
   every writer logged correctly.
2. **SQL:2011 system-versioned tables** (SQL Server, MariaDB, Db2; the `temporal_tables` extension on PostgreSQL).
   They provide transaction time automatically. PostgreSQL 17 has no native system versioning. The extension is
   unmaintained and not available on most managed services. It also leaves valid-time logic (`FOR PORTION OF`) to the
   application.
3. **PostgreSQL 18 temporal keys** (`PRIMARY KEY (..., valid_during WITHOUT OVERLAPS)`, `FOREIGN KEY ... PERIOD`).
   They enforce non-overlap on one range column. A bitemporal rule needs non-overlap on *two* ranges at once, which
   `WITHOUT OVERLAPS` cannot express.
4. **Event sourcing**: store every bulletin and correction as an event and derive views. That reproduces any past
   state, but every as-of query becomes a fold over the event stream, or a projection that has to be versioned
   itself. It is heavy for a table with one fact per series and day.
5. **One version table with a `daterange` for valid time, a `tstzrange` for known time, and a GiST exclusion
   constraint over both** (chosen).

## Decision

- `fx.rate_version` has one row per version: `series`, `valid_during daterange`, `known_during tstzrange`, the fact
  (`value`, `unit`, `observed_on`, `source`, `reason`) and `recorded_by`. Both ranges are half-open, `[from, to)`. An
  infinite upper bound means "until further notice" for valid time and "still believed" for known time.
- **The bitemporal invariant is a constraint, not a convention.**
  `EXCLUDE USING gist (series WITH =, valid_during WITH &&, known_during WITH &&)` guarantees that every
  `(series, date, knownAt)` question has at most one answer, whatever bug a writer has. `btree_gist` provides `=` on
  text inside the GiST index. A superuser creates it in the init script, because the migrator role is deliberately not
  a superuser.
- **History is append-only, enforced three ways.** (a) A trigger rejects `DELETE`, `TRUNCATE`, any change to a closed
  version, and any change other than closing `known_during`. (b) Column-level grants mean the application role
  `fx_app` has `SELECT`, `INSERT` and `UPDATE (known_during)` only. (c) The migrator owns the schema and runs only as a
  one-shot process. The trigger also stops the table owner. The integration tests run the destructive statements as
  both roles and expect them to fail.
- **One planner, two executions.** The sequenced update ("this fact holds for period P, as of now") is a pure function,
  `AssertionPlanner`, that returns which versions to close and which to insert. The remnants of a superseded version
  outside P are re-inserted with their original fact, so answers for unrelated dates never change. `InMemoryRateStore`
  and `PostgresRateStore` execute the same plans. FsCheck properties run random histories against the model, and an
  integration test runs the same random histories against PostgreSQL and requires identical answers on every probe.
- **All timestamps come from the database clock**, never from application servers, so there is one time line to
  reproduce against (see [ADR 0003](0003-knowledge-fence-for-reproducible-reads.md)).
- Plain SQL migrations, embedded and checksummed, instead of an ORM. The schema is mostly constraints, triggers and
  grants, which read more clearly written out than generated.

## Consequences

- Every as-of read is one indexed query: `series = $1 AND valid_during @> $2 AND known_during @> $3`. The exclusion
  constraint's GiST index serves it. The plan is recorded in [benchmarks](../benchmarks.md).
- Writes amplify. A new bulletin closes the open-ended version of the previous bulletin and re-inserts it with a closed
  upper bound. Ingesting the 28 recorded bulletins (1,876 observations) creates 3,685 versions. That is the price of
  answering "which rate applied on Sunday?" without application logic.
- A partial B-tree index on `(series, lower(valid_during)) WHERE upper_inf(known_during)` serves the writer's "current
  versions of this series" read without touching closed history.
- Moving to PostgreSQL 18 changes nothing here: `WITHOUT OVERLAPS` still covers only one period.

## Sources

- R. T. Snodgrass, *Developing Time-Oriented Database Applications in SQL* (1999). Terminology summarised in
  K. Torp, C. S. Jensen, R. T. Snodgrass, "Effective Timestamping in Databases",
  <https://homes.cs.aau.dk/~csj/Papers/Files/1997_torpTR-4.pdf>
- M. Fowler, "Bitemporal History", <https://www.martinfowler.com/articles/bitemporal-history.html>
- PostgreSQL 18 temporal constraints (`WITHOUT OVERLAPS`, `PERIOD`), <https://neon.com/postgresql/18/temporal-constraints>
  and Npgsql's notes, <https://www.npgsql.org/efcore/misc/temporal-constraints.html>
