# 0002: A bulletin is valid until the next one; corrections outrank the feed

- Status: accepted
- Date: 2026-11-12

## Context

A version needs a valid period, but sources do not publish periods. They publish bulletins: "on 30 October 2026 the
USD forex buying rate was 49.7747". Three different events all reach the store in that shape:

- **daily ingestion**: the newest bulletin;
- **late back-fill**: a bulletin for an older date that was missed, for example because the archive was unreachable;
- **correction**: an operator states that the value published for a date was wrong.

And there are no bulletins on weekends or public holidays (28–29 October 2026 is Republic Day). A valuation dated
Sunday still needs a rate.

## Options considered

1. **One version per calendar day.** Weekends and holidays would need synthetic copies, so every holiday calendar
   change would rewrite history. It also triples the row count for no information.
2. **Free-form valid periods in the API** ("this value holds for [D1, D2)"). This is maximally flexible, and it lets a
   caller assert periods that no bulletin ever had, which nobody can audit afterwards.
3. **Derive the period from the bulletin date** (chosen): a bulletin for D is asserted for `[D, next believed bulletin
   date)`, or `[D, infinity)` if D is the newest.

## Decision

- `ObservationPolicy.PeriodFor` maps a bulletin onto valid time as above. The same rule serves ingestion, back-fill and
  correction. That is why the API has no free-form valid-period write: a correction is "the bulletin for D said X",
  with a mandatory reason (10–500 characters, also a `CHECK` constraint).
- **Re-ingesting a believed bulletin is a no-op.** If the overlapping current versions already carry the same rate
  (value, unit, bulletin date) without gaps, nothing is written. Provenance does not count: the archive and EVDS
  reporting the same number is not a change. Without this rule, every scheduled re-run would churn known time and make
  "what changed?" useless.
- **One bulletin is written in one transaction, as its net effect** (`BatchPlanner`). All versions from one bulletin
  become known at the same instant. Applying its observations one by one at one instant would create versions
  superseded at the instant they were created (empty known periods, which the schema rejects), so the planner applies
  them in memory and writes only the survivors. A property test checks that a batch at one instant ends in the same
  current view as sequential writes.
- **A correction outranks the feed.** A feed observation (archive, EVDS, fixture) for a bulletin date whose current
  value is a correction is ignored. TCMB keeps publishing the original number, so without this rule the next
  scheduled ingest would quietly undo every correction. The compose demo checks this: after the demo's correction,
  `--ingest-fixtures` reports `changedBulletins: 0`. Withdrawing a correction is another correction, so it carries a
  reason and an author in the audit trail.

## Consequences

- A back-filled bulletin splits the carried-over version: `[D_prev, infinity)` becomes `[D_prev, D_missing)` and
  `[D_missing, infinity)`. Answers for dates before `D_missing` do not change. The scenario tests cover this.
- A correction for D changes the answer for D and the non-business days it was carried over to, and nothing else.
  `GET /rates/{series}/changes` lists exactly those dates.
- The precedence rule is coarse on purpose. A feed value that really did change after a correction (TCMB re-publishing
  a bulletin, which has not happened in the recorded period) also needs an operator, who records it as a correction.
  That is the conservative choice for financial reporting.
- Valid time has business-date granularity. Intraday rates are out of scope (see the README's Limitations).
