# 0004: Three rate sources behind one interface; real recorded bulletins for tests and the demo

- Status: accepted
- Date: 2026-11-12

## Context

TCMB publishes its indicative exchange rates in two ways:

- **The daily XML bulletin archive** at `https://www.tcmb.gov.tr/kurlar/YYYYMM/DDMMYYYY.xml` (`today.xml` for the
  latest). It is public, needs no key, and gives forex and banknote buying/selling rates per currency, with a `Unit`
  (JPY is quoted per 100).
- **EVDS** (Elektronik Veri Dağıtım Sistemi, electronic data delivery system), a web service with series codes such as
  `TP.DK.USD.A.YTL`. It needs a personal API key. TCMB opened EVDS 3 in beta in January 2026, with EVDS 2 kept
  running alongside it. The EVDS 2 guide says the key travels in a `key` HTTP header, and requests without it get 403.

Tests must not depend on the network or on a key. This project has **no EVDS key**.

## Options considered

1. **Synthetic rates only.** Fully offline, but parser bugs against the real format (Turkish names, empty cross-rate
   elements, CRLF, BOM, holiday gaps) would go unnoticed.
2. **Live calls in tests.** Realistic and flaky, and impossible for EVDS without a key.
3. **Record real bulletins once, replay them** (chosen for the archive). EVDS is tested against fixtures *shaped like*
   its documented responses, and that limitation is stated wherever it matters.

## Decision

- `IRateSource` has three implementations: `TcmbArchiveSource` (public XML; non-business days return 404 and are
  skipped), `EvdsSource` (key in the `key` header, never in the URL where it would reach logs), and
  `RecordedBulletinSource`.
- `fixtures/tcmb/` holds **28 real bulletins** (1–30 October and 2–11 November 2026; 28–29 October was a holiday),
  downloaded from the public archive and kept byte for byte (`.gitattributes` marks them `-text`). They are embedded in
  the Sources assembly. A golden file pins every observation parsed from them (1,876 lines), so a parser change that
  alters a single value fails a test. Spot check while finishing this repo: the archive's `30102026.xml` was
  downloaded again and was byte-identical to the fixture.
- `tests/.../Fixtures/evds/usd-eur-2026-10-01_05.json` is **not a live EVDS capture**. It follows the documented shape
  (`items[]`, `Tarih` as `dd-MM-yyyy`, one property per series with dots replaced by underscores, `null` on
  non-business days). Its values are copied from the real bulletins, so a test can check that both sources agree.
- HTTP clients use `Microsoft.Extensions.Http.Resilience` (timeouts, retries with jitter, circuit breaker). Fetching a
  bulletin is idempotent, and so is recording it (see [ADR 0002](0002-bulletin-validity-and-correction-precedence.md)).
- The compose demo and the integration tests use the recorded source. `tcmb-archive` works without configuration.
  `evds` needs `Sources__EvdsApiKey`.

## Consequences

- The EVDS client has never been exercised against the live service. If EVDS 3 changed the response shape or the key
  mechanism, the client needs an update, and the README says so.
- The archive URL pattern and XML format are not formally specified by TCMB. They have been stable for years, and the
  parser fails loudly (`RateSourceException`) instead of guessing.
- Synthetic data is used elsewhere: benchmark series (`TP.DK.SAA.A.YTL`, ...), test-only series (`TP.DK.QAA...`), and
  every correction value, which is labelled `synthetic` in its reason.

## Sources

- EVDS Web Service Usage Guide (EVDS 2), <https://evds2.tcmb.gov.tr/help/videos/EVDS_Web_Service_Usage_Guide.pdf>
- TCMB press release on the EVDS renewal (January 2026), summarised at
  <https://www.alomaliye.com/2026/01/26/tcmb-elektronik-veri-dagitim-sisteminin-yenilenmesine-iliskin-basin-duyurusu/>
- CBRT R package news (moved to the EVDS 3 server without parameter changes),
  <https://ftp.fau.de/cran/web/packages/CBRT/news/news.html>
