#!/usr/bin/env bash
# End-to-end smoke test against a running SmartDispenser instance.
# Usage: scripts/smoke.sh [base-url]   (WEBHOOK_SECRET must match the Webhook:Secret of the server)
set -euo pipefail

BASE="${1:-http://localhost:5000}"
SECRET="${WEBHOOK_SECRET:?set WEBHOOK_SECRET to the Webhook:Secret of the server}"

fail() { echo "FAIL: $*" >&2; exit 1; }
expect_status() {
  local expected="$1" actual="$2" what="$3"
  [[ "$actual" == "$expected" ]] || fail "$what: expected HTTP $expected, got $actual"
  echo "ok   $what -> $actual"
}

for _ in $(seq 1 60); do
  curl -fs "$BASE/health/live" >/dev/null && break
  sleep 1
done

expect_status 200 "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health/live")" "GET /health/live"
expect_status 200 "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health/ready")" "GET /health/ready (database)"
expect_status 200 "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/swagger/index.html")" "GET /swagger/index.html"
curl -fs "$BASE/openapi/v1.json" | jq -e '.paths["/api/dispensers/{id}/dispense"].post' >/dev/null \
  || fail "OpenAPI document does not describe POST /api/dispensers/{id}/dispense"
echo "ok   GET /openapi/v1.json describes the dispense endpoint"

NAME="Smoke-$RANDOM"
CREATED=$(curl -fs -X POST "$BASE/api/dispensers" -H 'Content-Type: application/json' \
  -d "{\"name\":\"$NAME\",\"location\":\"CI\",\"inventoryCapacity\":20}")
ID=$(jq -r .id <<<"$CREATED")
[[ "$(jq -r .status <<<"$CREATED")" == "Offline" ]] || fail "new dispenser should be Offline: $CREATED"
echo "ok   POST /api/dispensers -> $ID (Offline)"

curl -fs "$BASE/dashboard" | grep -q "$NAME" || fail "dashboard does not list $NAME"
echo "ok   GET /dashboard lists $NAME"

STATUS=$(curl -s -o /tmp/smoke-dispense.json -w '%{http_code}' --max-time 60 -X POST "$BASE/api/dispensers/$ID/dispense" \
  -H 'Content-Type: application/json' -d '{"prescriptionId":"RX-SMOKE","patientIdentifier":"PATIENT-1"}')
case "$STATUS" in
  503) echo "ok   POST dispense with no orchestrator running -> 503: $(jq -r .detail /tmp/smoke-dispense.json)" ;;
  422) echo "ok   POST dispense rejected by the orchestrator -> 422: $(jq -r .detail /tmp/smoke-dispense.json)" ;;
  *) fail "POST dispense: expected 503 or 422, got $STATUS $(cat /tmp/smoke-dispense.json)" ;;
esac

BODY='{"transactionId":"TX-SMOKE","idempotencyKey":"idem-smoke-'$RANDOM'","dispenseRequestId":"00000000-0000-0000-0000-000000000000","amount":1.0,"currency":"EUR","status":"confirmed","provider":"sandbox","timestamp":"2026-01-01T00:00:00Z"}'
expect_status 401 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/payments/webhook" \
  -H 'Content-Type: application/json' -H 'X-Signature-SHA256: sha256=forged' -d "$BODY")" "POST webhook with a forged signature"
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -hex | awk '{print $NF}')
ACK=$(curl -fs -X POST "$BASE/api/payments/webhook" -H 'Content-Type: application/json' \
  -H "X-Signature-SHA256: sha256=$SIG" -d "$BODY")
[[ "$ACK" == '{"received":true,"duplicate":false}' ]] || fail "signed webhook ack: $ACK"
echo "ok   POST webhook with a valid signature -> $ACK"

echo "smoke test passed against $BASE"
