# 0003: A knowledge fence makes "as known at K" answers final

- Status: accepted
- Date: 2026-11-12

## Context

The promise of this service is that a report pinned to known time K returns the same bytes forever. Corrections
recorded later must not change it. The bitemporal table alone does not keep that promise under concurrency.

A writer has to stamp its versions with a known-time instant *before* it commits (the instant goes into the rows it
inserts). In PostgreSQL that instant is usually `now()`, the transaction start. Picture a reader that runs between the
writer's stamp and the writer's commit. It reads the clock (K = 10:00:00.200), queries, and does not see the
uncommitted rows. A moment later the writer commits rows "known from 10:00:00.100". Asking the same question as of K
now returns a different answer, and the report that was filed can no longer be reproduced.

This is the classic transaction-timestamping problem. Timestamps must agree with the commit (serialization) order.
Jensen & Lomet (VLDB 2001) and Torp, Jensen & Snodgrass describe it, and Lomet proposed using the lock manager to
choose timestamps. The integration test `Naive_stamping_lets_a_past_answer_change` reproduces it on PostgreSQL 17 by
holding a writer's commit back for 1.5 s.

## Options considered

1. **Ignore it** (what most implementations do). The window is a few milliseconds, but in the test above the anomaly
   happens every time, and the cost is an audit finding that cannot be explained.
2. **Report a known time in the past** (for example `now() - 5 s`). This narrows the window without closing it: a
   writer stalled for longer still breaks the promise, and every "now" answer is stale by design.
3. **SERIALIZABLE isolation.** It orders transactions, but `now()` is still taken at transaction start, so stamp order
   and serialization order can still disagree. Readers would also take serialization failures and have to retry.
4. **Commit-time stamping.** Insert with a placeholder, then rewrite the timestamp at commit. PostgreSQL has no
   commit-time hook that can update rows, and rewriting known time contradicts append-only history.
5. **A per-series lock fence** (chosen). Writers take per-series advisory locks exclusively *before* reading the
   clock. Readers take the same locks shared, *then* read the clock, and use it as their knowledge horizon.

## Decision

- `fx.lock_series(text[], shared)` takes transaction-scoped advisory locks (`pg_advisory_xact_lock[_shared]`) on
  `hashtextextended(series)`, always in ascending key order. A multi-series writer (one bulletin, about 67 series) and
  a multi-series reader (a report) therefore cannot deadlock.
- **Writer:** lock (exclusive), then `clock_timestamp()`, plan, write, commit. The instant is also forced strictly
  above every current version's start, so a clock step back can never produce an empty known period.
- **Reader:** lock (shared), then `clock_timestamp()` as the horizon, query, commit. A requested `knownAt` later than
  the horizon is refused with 400: that answer could still change. Every response carries `X-Known-At`. Sending it back
  as `knownAt` gives the identical answer.
- **Why it holds:** a writer that has stamped instant W still holds its exclusive lock. A reader whose horizon H could
  be later than W has to acquire the shared lock first, so it waits for that commit and sees the rows. A writer that
  locks after the reader commits reads the clock later than H, so its rows are known after H. Either way, no row
  becomes visible later with a known-from at or before an answered horizon.
- `lock_timeout = 5 s` bounds waits. `idle_in_transaction_session_timeout = 30 s` stops a stalled client from holding a
  fence. The mode is configurable (`Store:Consistency = Fenced | Naive`) only so that tests and benchmarks can show the
  difference. Production uses `Fenced`.

## Consequences

- `The_fence_makes_the_reader_wait_and_the_answer_never_changes` shows the reader blocking for the writer's held-back
  commit and then reproducing its answer exactly. `Under_concurrent_corrections_every_fenced_answer_is_reproducible`
  runs 3 writers and 6 readers for 4 s and re-asks every recorded `(knownAt, answer)` pair: zero mismatches.
- Cost: one extra statement in the same round trip on reads (lock and clock are batched), and readers of a series wait
  while that series is being written. Bulletins arrive once a day and corrections are rare, so contention is low in
  practice. The worst case (a writer correcting the same series in a tight loop) is measured in
  [benchmarks](../benchmarks.md).
- The guarantee depends on every writer taking the lock. Only `PostgresRateStore` writes, the app role cannot write
  any other way, and the store fails loudly if it closes fewer versions than it planned.
- Advisory-lock keys are 64-bit hashes. A collision between two series only adds waiting, never wrong answers.

## Sources

- C. S. Jensen, D. Lomet, "Transaction Timestamping in (Temporal) Databases", VLDB 2001,
  <https://homes.cs.aau.dk/~csj/Papers/Files/2001_jensenVLDB.pdf>
- D. Lomet et al., "Using the Lock Manager to Choose Timestamps" (Microsoft Research),
  <https://microsoft.com/en-us/research/?p=155687>
- K. Torp, C. S. Jensen, R. T. Snodgrass, "Effective Timestamping in Databases",
  <https://homes.cs.aau.dk/~csj/Papers/Files/1997_torpTR-4.pdf>
