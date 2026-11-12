# Benchmarks

Every number here was measured on the machine described below, on 2026-11-12. Nothing is estimated or extrapolated.
Where a run was noisy, both runs are shown.

## Environment

| | |
|---|---|
| CPU | 13th Gen Intel Core i7-13620H, 10 cores / 16 logical processors |
| RAM | 15.7 GB (host); the Docker Desktop VM has 7.6 GB |
| OS | Windows 11 Home 10.0.26200 |
| Runtime | .NET SDK 10.0.401, runtime 10.0.12, Release build |
| Docker | Docker Desktop, engine 29.9.1, WSL2 kernel 6.18 |
| PostgreSQL | `postgres:17-alpine`, default configuration, via Testcontainers (store) or `docker compose` (HTTP) |
| Host load | Not idle. About 60% CPU load reported when the runs started. Other projects' containers (a FHIR server, Oracle Free and others) and another agent's builds were running at the same time. During run 2 of the store scenario, a `dotnet format` run overlapped the ingest phase. |

Client and database talk over Docker Desktop's port forwarding, which adds a large part of every round trip. Treat
absolute latencies as an upper bound and the *differences* (fenced vs naive, first vs last bulletins) as the result.

## How to reproduce

```bash
# micro-benchmarks (BenchmarkDotNet, no I/O)
dotnet run -c Release --project benchmarks/FxBitemporal.Benchmarks -- micro --filter '*'
# PostgreSQL store (starts its own postgres:17-alpine through Testcontainers)
SAMPLES=2000 dotnet run -c Release --project benchmarks/FxBitemporal.Benchmarks -- store
# HTTP, against the compose stack
docker compose up -d && SAMPLES=1000 dotnet run -c Release --project benchmarks/FxBitemporal.Benchmarks -- http
```

Raw results are written to `benchmark-results/*.json`. The committed files are from the runs reported here (store:
run 2).

## Store scenario

**Dataset.** 20 synthetic series (`TP.DK.SAA.A.YTL` ...), 520 business-day bulletins (2024-11-04 onwards, two years),
generated as a seeded random walk. Each bulletin is written in one transaction, as the service writes it. Then about
1% of (series, bulletin) pairs (117) get a correction, one transaction each, in random order. Result: 20,897
versions, table 3.3 MiB, indexes 4.1 MiB. The database is set up exactly as in production: the init script, migrations
as `fx_migrator`, and every query as `fx_app`.

**Method.** Latency is measured client-side with `Stopwatch` around one store call (a whole transaction including round
trips). Reads are sequential, with random series, date and (for pinned reads) a random past known time from the load.
There are 200 warm-up calls that are not recorded. Percentiles are nearest-rank (always an observed sample).
Contention: 4 reader tasks ask "now" for one series while 1 writer records corrections to that same series in a tight
loop, for 5 s.

| Measurement | Run 1 median (p95) | Run 2 median (p95) |
|---|---|---|
| Bulletin write, 20 series (n=520) | 65.1 ms (89.9) | 65.6 ms (119.3) |
| first 50 bulletins / last 50 bulletins | 71.9 / 65.2 ms | 59.9 / 66.1 ms |
| Total load of 520 bulletins | 39.5 s | 40.1 s |
| Correction write (n=117) | 8.0 ms (12.6) | 5.5 ms (12.4) |
| As-of "now", fenced (n=2000) | 1.91 ms (3.27) | 2.66 ms (6.24) |
| As-of "now", naive (n=2000) | 1.41 ms (2.10) | 1.60 ms (2.39) |
| As-of pinned known time, fenced (n=2000) | 2.10 ms (3.65) | 1.66 ms (2.36) |
| 12-series report read, one fence (n=500) | 2.34 ms (4.14) | 2.22 ms (3.02) |
| Contended reads, fenced | 7.84 ms (12.77), 471/s; writer 118/s | 8.91 ms (15.85), 418/s; writer 104/s |
| Contended reads, naive | 2.16 ms (3.25), 1,737/s; writer 105/s | 2.46 ms (4.41), 1,439/s; writer 88/s |

**As-of query plan** (`EXPLAIN (ANALYZE, BUFFERS)` of
`series = $1 AND valid_during @> $2 AND known_during @> $3` on the loaded table): Bitmap Heap Scan over a Bitmap Index
Scan on `no_bitemporal_overlap` (the exclusion constraint's GiST index), 5–6 shared buffers, 0.52 ms (run 1) and 0.81 ms
(run 2) execution. No extra index is needed for reads.

**Where the write time goes.** On a scratch `postgres:17-alpine` with the same schema and 10,400 current versions,
`EXPLAIN ANALYZE` measured: closing 20 versions (UPDATE of `known_during`) took 13.0 ms, 4.0 ms of it in the
append-only trigger. Inserting 20 versions took 5.3 ms. The writer's clock-guard query (max known-from over current
versions) took 2.2 ms. Index maintenance and exclusion checks on GiST dominate. The guard query is the one part that
grows with history (see README, Limitations).

### Reading the numbers

- The fence costs about 0.5 ms (run 1) to 1 ms (run 2) per uncontended read: one extra statement in the same round
  trip, plus the lock.
- Under deliberate same-series contention, fenced readers wait for the writer's commit. Reads are about 3.6× slower and
  about 3.5× fewer per second. That is the mechanism working: those reads would otherwise risk answers that change.
  In production a series is written once per business day.
- Write latency is flat across two years of history.

## HTTP scenario

Sequential requests from the host to the compose stack (Kestrel in the chiseled container, the API as `fx_app`, the
recorded bulletins loaded), 100 warm-up requests, n=1000 each.

| Request | Median | p95 | p99 |
|---|---|---|---|
| `GET /api/v1/rates/TP.DK.USD.A.YTL?date=…` (as of now) | 4.92 ms | 26.30 ms | 54.71 ms |
| `POST /api/v1/reports/revaluation` (3 positions) | 4.07 ms | 17.11 ms | 37.90 ms |

The wide tails come from the shared host. The medians include two hops through Docker Desktop's port forwarding.

## Micro-benchmarks (BenchmarkDotNet 0.15.8, in-process, no I/O)

| Operation | Median | Mean ± StdDev | Allocated |
|---|---|---|---|
| Parse one TCMB bulletin (XML, about 9 KB, 67 series × 4 rates) | 86.1 µs | 87.0 ± 8.3 µs | 68.0 KB |
| Plan one bulletin write (67 series, `BatchPlanner`) | 94.7 µs | 94.7 ± 7.1 µs | 211.0 KB |
| Build and hash a 12-position revaluation report (canonical JSON + SHA-256) | 14.4 µs | 14.9 ± 2.2 µs | 15.9 KB |
| Diff a 40-day timeline between two known times | 3.5 µs | 3.5 ± 0.4 µs | 5.0 KB |

BenchmarkDotNet default job, Release, .NET 10.0.12, the same shared host. BenchmarkDotNet flagged a multimodal
distribution for the parse and report benchmarks, which is consistent with the background load. Raw output:
`benchmark-results/micro.md`. CPU work per bulletin (parse + plan, about 0.2 ms) is negligible next to the database
transaction (about 65 ms).

## Not measured

Multi-instance deployments, histories beyond about 21k versions, tuned PostgreSQL settings, and network latency to
the real TCMB endpoints.
