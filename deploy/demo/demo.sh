#!/bin/sh
# The compose demo, end to end, with nothing but BusyBox (wget, sha256sum, grep, sed):
#   1. value three FX positions at 31 October 2026 "as known now" and remember the known time K the API reports
#   2. record a (synthetic) correction of the 30 October EUR bulletin
#   3. ask for the same report as of K: it must be byte-identical, and its SHA-256 must match the first one
#   4. ask for the report as of now: it must differ, and use the corrected EUR rate
#   5. ask which dates' answers changed between K and now
# Exits 0 only if every check holds. Safe to re-run: a correction that is already believed is a no-op.
set -eu

API="${API_URL:-http://api:8080}"
KEY="${ADMIN_KEY:-admin-dev-only}"
WORK=$(mktemp -d)
FAILURES=""

fail() { FAILURES="${FAILURES}${FAILURES:+, }\"$1\""; echo "CHECK FAILED: $1" >&2; }

header() { sed -n "s/^ *$1: *//Ip" "$2" | tr -d '\r' | tail -n 1; }

# POST JSON; body to $1.body, response headers to $1.headers.
post() {
    wget -q -S -O "$WORK/$1.body" --header "Content-Type: application/json" ${3:+--header "$3"} \
        --post-data "$2" "$API$4" 2>"$WORK/$1.headers"
}

echo "waiting for $API/health/ready"
i=0
until wget -q -O /dev/null "$API/health/ready" 2>/dev/null; do
    i=$((i + 1))
    [ "$i" -le 60 ] || { echo "API not ready after 60 s" >&2; exit 1; }
    sleep 1
done

POSITIONS='[{"currency":"USD","amount":125000},{"currency":"EUR","amount":-40000.50},{"currency":"JPY","amount":3000000}]'
REPORT_URL=/api/v1/reports/revaluation

# 1. The report as known now.
post first "{\"valuationDate\":\"2026-10-31\",\"positions\":$POSITIONS}" "" "$REPORT_URL"
K=$(header X-Known-At "$WORK/first.headers")
H1=$(header X-Report-Sha256 "$WORK/first.headers")
echo "report as known at $K: sha256 $H1"
cat "$WORK/first.body"; echo
[ "$(sha256sum "$WORK/first.body" | cut -d' ' -f1)" = "$H1" ] || fail "body hashes to X-Report-Sha256"

# 2. A correction (synthetic: the published value is fine; the demo pretends it was not).
sleep 1
post correction '{"series":"TP.DK.EUR.A.YTL","bulletinDate":"2026-10-30","value":55.9149,"reason":"demo: synthetic correction of the 30 October EUR forex buying rate"}' \
    "X-Admin-Key: $KEY" /api/v1/admin/corrections
echo "correction: $(cat "$WORK/correction.body")"
CHANGED=false
grep -q '"changed":true' "$WORK/correction.body" && CHANGED=true

# 3. The same report, pinned to K.
post replay "{\"valuationDate\":\"2026-10-31\",\"knownAt\":\"$K\",\"positions\":$POSITIONS}" "" "$REPORT_URL"
H_REPLAY=$(header X-Report-Sha256 "$WORK/replay.headers")
echo "report replayed as of $K: sha256 $H_REPLAY"
cmp -s "$WORK/first.body" "$WORK/replay.body" || fail "replay as of K is byte-identical"
[ "$H_REPLAY" = "$H1" ] || fail "replay hash equals the original hash"

# 4. The report as known now, after the correction.
post now "{\"valuationDate\":\"2026-10-31\",\"positions\":$POSITIONS}" "" "$REPORT_URL"
H_NOW=$(header X-Report-Sha256 "$WORK/now.headers")
echo "report as known now: sha256 $H_NOW"
grep -q '"currency":"EUR"[^}]*"rate":"55.9149"' "$WORK/now.body" || fail "report as of now uses the corrected EUR rate"
if [ "$CHANGED" = true ] && [ "$H_NOW" = "$H1" ]; then fail "report as of now differs after a correction"; fi

# 5. What changed between K and now?
wget -q -O "$WORK/changes.body" "$API/api/v1/rates/TP.DK.EUR.A.YTL/changes?from=2026-10-28&to=2026-11-03&knownA=$(echo "$K" | sed 's/:/%3A/g')"
echo "changes since $K: $(cat "$WORK/changes.body")"

echo "{\"demo\":\"fx-bitemporal\",\"knownAt\":\"$K\",\"correctionChanged\":$CHANGED,\"sha256AsOfK\":\"$H1\",\"sha256Replay\":\"$H_REPLAY\",\"sha256Now\":\"$H_NOW\",\"reproducible\":$([ "$H_REPLAY" = "$H1" ] && echo true || echo false),\"failures\":[$FAILURES]}"
[ -z "$FAILURES" ]
