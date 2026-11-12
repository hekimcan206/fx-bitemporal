# EVDS fixtures

`usd-eur-2026-10-01_05.json` is **shaped like** an EVDS `type=json` response (an `items` array, `Tarih` as dd-MM-yyyy,
one property per series with dots replaced by underscores, `null` on non-business days). It was not captured from the
live EVDS service: this project has no EVDS API key. The values are copied from the real TCMB bulletins for the same
dates in `fixtures/tcmb/`, so a test can check that both sources agree. See `docs/adr/0004-rate-sources-and-fixtures.md`.
