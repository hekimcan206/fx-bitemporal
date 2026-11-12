# 0005: Reports are canonical JSON, identified by their SHA-256

- Status: accepted
- Date: 2026-11-12

## Context

"The report is reproducible" has to be checkable by someone who does not trust this service: an auditor, or the
finance team comparing a filed PDF with a re-run. Comparing parsed JSON field by field leaves room for argument about
number formatting, property order and time zones. `50.10780` and `50.1078` are the same rate. `2026-11-03T12:30:00+03:00`
and `2026-11-03T09:30:00Z` are the same instant.

## Options considered

1. **Plain JSON from the default serializer** plus a "report id" stored server-side. That requires trusting the
   server's storage, and serializer upgrades can change bytes.
2. **Signed reports** (JWS). These prove origin, but need key management, which is out of scope for this project.
3. **A canonical byte form and its SHA-256** (chosen). Equal hashes mean byte-identical reports, and anyone can
   recompute the hash from the body.

## Decision

- `Canonical` defines one textual form per value: invariant culture, decimals without exponent or trailing zeros,
  instants in UTC with microseconds (the precision of `timestamptz`), and dates as `yyyy-MM-dd`. Numbers are written as
  JSON strings, so no client parses them into binary floating point.
- `RevaluationBuilder` sorts lines by currency, rounds TRY amounts to 2 decimals (away from zero), records for every
  line the version id, bulletin date and the instant that version became known, and marks lines without a rate as
  `no-rate` (with `complete: false`) instead of failing. The response body **is** the canonical document, and
  `X-Report-Sha256` is its hash.
- A golden test pins the exact bytes and hash of a sample report. The integration test and the compose demo check that
  the same request with the same `knownAt` returns the same bytes after a correction, and different bytes as of now.

## Consequences

- Changing the canonical form (a new field, different rounding) changes every hash. That is a breaking change and needs
  a new report version. The golden test makes sure it cannot happen by accident.
- The hash proves equality, not origin. Signing is listed under next steps.
