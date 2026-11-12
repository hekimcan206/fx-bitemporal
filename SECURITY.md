# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| History integrity | Append-only trigger (also stops the table owner) and a GiST exclusion constraint over valid and known time |
| Database access | The service runs as `fx_app` (`SELECT`, `INSERT`, `UPDATE (known_during)` only). Migrations run as `fx_migrator` in a one-shot process |
| Known time | Stamped by the database clock under a per-series lock fence; never accepted from callers |
| Writes | Ingestion and corrections only, behind `X-Admin-Key` (constant-time comparison); disabled when no key is configured |
| Audit trail | Every correction needs a 10–500 character reason; every version records source and `recorded_by` |
| Report integrity | Canonical JSON body with `X-Report-Sha256` |
| Input limits | 256 KiB body, 200 report positions, bounded date ranges, strict series-code pattern |
| Secrets | None in the repository. `.env.example` holds clearly marked development-only values. The EVDS key travels in a header and is never logged |
