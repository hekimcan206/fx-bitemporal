# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-12

### Added

- Bitemporal rate model: half-open valid periods (business dates) and known periods (database instants), and a pure
  sequenced-update planner shared by an in-memory reference model and the PostgreSQL store.
- Bulletin validity policy: a bulletin applies until the next believed bulletin, which covers weekends, holidays and
  late back-fills. Re-ingesting a believed bulletin is a no-op, one bulletin is written as one net-effect batch, and
  a feed observation never undoes a correction.
- PostgreSQL 17 store: `daterange`/`tstzrange` versions, a GiST exclusion constraint over both periods, an append-only
  trigger, column-level grants, checksummed SQL migrations run as a separate owner role.
- Knowledge fence: per-series advisory locks (exclusive for writers, shared for readers, ascending order) so that an
  answer given as of known time K never changes; the naive mode is kept to demonstrate the anomaly.
- HTTP API: as-of rate, audit history, changes between two known times, and a revaluation report whose canonical JSON
  body is identified by `X-Report-Sha256`; admin ingestion (fixture, TCMB archive, EVDS) and corrections with
  mandatory reasons. OpenAPI + Scalar, problem details, Serilog JSON logs, OpenTelemetry, health probes.
- Rate sources: TCMB daily XML archive (public), EVDS client (key in a header), and 28 real bulletins recorded from
  the archive (October–November 2026) with a golden file of all 1,876 parsed observations.
- Tests: unit and scenario tests, FsCheck properties (no overlap, reproducible past answers, idempotent replay, batch
  equals sequential), golden tests, and Testcontainers integration tests including model agreement on random
  histories, database-enforced invariants, the HTTP contract and fence tests under concurrency.
- `docker compose up` demo that records a correction and proves the earlier report is byte-identical; benchmarks
  (BenchmarkDotNet, store on PostgreSQL 17, HTTP); ADRs 0001–0005, threat model; CI with vulnerable-package audit,
  format check, coverage, container build and compose demo; CodeQL.

[Unreleased]: https://github.com/hekimcan206/fx-bitemporal/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/fx-bitemporal/releases/tag/v1.0.0
