# Threat model

Scope: the fx-bitemporal API, its PostgreSQL database, and the ingestion path from TCMB sources. The data is public
(published exchange rates) and contains no personal data. What needs protecting is **integrity and auditability**: a
report filed as of known time K must stay reproducible, and every change of belief must be attributable.

## Assets

| Asset | Why it matters |
|---|---|
| Version history (`fx.rate_version`) | The basis of every reproducible report. Losing or editing it silently breaks audits |
| Correction reasons and `recorded_by` | The audit trail: who changed which belief, and why |
| Admin key | Authorises ingestion and corrections (the only writes) |
| EVDS API key (optional) | A personal credential issued by TCMB |
| Database credentials | `fx_migrator` (DDL) and `fx_app` (DML) |

## Actors

- **Report consumers** (finance systems, auditors): read-only HTTP clients.
- **Operators**: hold the admin key and record corrections.
- **Upstream sources**: the TCMB archive and EVDS, reached over HTTPS and trusted only for their content format.
- **Attackers**: anyone who can reach the HTTP port, a compromised upstream or network path, or a buggy or malicious
  process with database access.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation | Residual risk |
|---|---|---|---|
| S1 | Spoofing an operator to record a fake correction | Admin endpoints need `X-Admin-Key`. It is compared in constant time over SHA-256 digests, and an empty key disables the endpoints | A single shared key: no per-operator identity (see next steps) |
| T1 | Editing or deleting history to make a past report "never have happened" | Append-only trigger (rejects DELETE, TRUNCATE, any change to closed versions and any change other than closing `known_during`), even for the owner. `fx_app` holds only `SELECT, INSERT, UPDATE (known_during)`. Integration tests run the attacks as both roles | A database superuser can disable triggers. Use managed-DB controls and backups |
| T2 | Two versions answering the same question (a corrupted invariant) | GiST exclusion constraint over `(series, valid_during, known_during)`. The store fails loudly if it closes fewer rows than it planned | None known |
| T3 | Backdating knowledge: inserting a version "known" before an answer already given | Known time comes only from the database clock under the knowledge fence ([ADR 0003](adr/0003-knowledge-fence-for-reproducible-reads.md)). Callers cannot supply it. A future `knownAt` is refused | A database clock stepped back is countered by forcing each instant above the current versions' starts |
| T4 | A tampered upstream response (MITM, compromised source) | HTTPS only. The parser rejects unknown structure, non-positive or out-of-range values and invalid series codes. CHECK constraints repeat the rules. Every version records its `source` | A plausible but wrong value from a compromised TCMB endpoint is ingested. Corrections and the changes endpoint make that visible and reversible |
| R1 | An operator denies making a correction | `reason` (10–500 characters, enforced by CHECK), `recorded_by`, and the database-stamped known time on every version | `recorded_by` names the role (`admin:correction`), not a person, until per-user auth exists |
| R2 | A report consumer disputes what was returned | The response body is canonical JSON with `X-Report-Sha256`. The same request with `X-Known-At` reproduces the same bytes | The hash proves equality, not origin. Signing is a next step |
| I1 | EVDS key disclosure | Sent in a request header, never in the URL. Never logged or returned. Supplied through the environment only | Visible to anyone with access to the container environment |
| I2 | Credential leakage through the repository | No secrets committed. `.env.example` holds clearly marked development-only values. Compose binds ports to loopback | Development defaults must be overridden in any shared deployment |
| D1 | Large requests or ranges exhausting the service | 256 KiB body limit. At most 200 report positions. Ingest range of 1–400 days, changes range of at most 3,660 days. Parameterised, index-backed queries | No rate limiting (place behind a gateway) |
| D2 | A stalled client holding a fence and blocking writers | Transaction-scoped locks, `lock_timeout = 5 s`, `idle_in_transaction_session_timeout = 30 s`. The application never holds a fence across I/O to the client | A burst of contended reads slows writers on the same series (measured in [benchmarks](benchmarks.md)) |
| E1 | SQL injection leading to privilege use | Every statement is parameterised. Series codes are also validated against a strict pattern in the code and in a CHECK | None known |
| E2 | Container escape or misuse | Chiseled non-root image (no shell or package manager). CI verifies the image user is not root | Standard container-runtime risk |

## Out of scope

Authentication for read endpoints (rates are public data), multi-tenant separation, and key management for report
signing.
