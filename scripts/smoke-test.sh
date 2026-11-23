#!/usr/bin/env bash
# End-to-end smoke test against a running Ledgerline API.
#
#   scripts/smoke-test.sh [base-url]        (default http://localhost:8080)
#
# Posts a deposit, a bet, a replay of the bet and a win, then checks balances, the statement, the trial
# balance and the round summary that arrives asynchronously through the outbox and the projection.
# Needs curl and jq.
set -euo pipefail

BASE="${1:-http://localhost:8080}"
RUN="smoke-$(date +%s)-$RANDOM"
PLAYER="p-${RUN}"
ROUND="r-${RUN}"

fail() { echo "FAIL: $*" >&2; exit 1; }

post() {
  curl -sS -o /tmp/body.json -w '%{http_code}' -D /tmp/headers.txt -X POST "$BASE$1" \
    -H 'Content-Type: application/json' -d "$2"
}

echo "Waiting for $BASE/health/ready"
for _ in $(seq 1 60); do
  if curl -fsS "$BASE/health/ready" > /dev/null 2>&1; then break; fi
  sleep 1
done
curl -fsS "$BASE/health/ready" | jq -c . || fail "API never became ready"

status=$(post /v1/deposits "{\"transactionId\":\"${RUN}-dep\",\"provider\":\"northwind-pay\",\"playerId\":\"$PLAYER\",\"currency\":\"EUR\",\"amountMinor\":10000}")
[ "$status" = 201 ] || fail "deposit returned $status: $(cat /tmp/body.json)"

bet="{\"transactionId\":\"${RUN}-bet\",\"playerId\":\"$PLAYER\",\"roundId\":\"$ROUND\",\"currency\":\"EUR\",\"amountMinor\":2500}"
status=$(post /v1/wallet/bets "$bet")
[ "$status" = 201 ] || fail "bet returned $status: $(cat /tmp/body.json)"
entry=$(jq -r .entryId /tmp/body.json)

status=$(post /v1/wallet/bets "$bet")
[ "$status" = 200 ] || fail "bet replay returned $status"
grep -qi '^idempotent-replayed: true' /tmp/headers.txt || fail "bet replay is missing the Idempotent-Replayed header"
[ "$(jq -r .entryId /tmp/body.json)" = "$entry" ] || fail "bet replay returned a different entry"

status=$(post /v1/wallet/wins "{\"transactionId\":\"${RUN}-win\",\"playerId\":\"$PLAYER\",\"roundId\":\"$ROUND\",\"currency\":\"EUR\",\"amountMinor\":6000}")
[ "$status" = 201 ] || fail "win returned $status: $(cat /tmp/body.json)"

status=$(post /v1/wallet/bets "{\"transactionId\":\"${RUN}-big\",\"playerId\":\"$PLAYER\",\"roundId\":\"$ROUND\",\"currency\":\"EUR\",\"amountMinor\":999999}")
[ "$status" = 422 ] || fail "overdraft returned $status, expected 422"
[ "$(jq -r .code /tmp/body.json)" = insufficient-funds ] || fail "overdraft problem has the wrong code"

wallets=$(curl -fsS "$BASE/v1/players/$PLAYER/wallets")
[ "$(echo "$wallets" | jq -r '.wallets[0].balance.minor')" = 13500 ] || fail "unexpected balance: $wallets"
account=$(echo "$wallets" | jq -r '.wallets[0].accountId')

statement=$(curl -fsS "$BASE/v1/accounts/$account/statement?limit=2")
[ "$(echo "$statement" | jq '.items | length')" = 2 ] || fail "statement page size: $statement"
cursor=$(echo "$statement" | jq -r .nextCursor)
[ "$(curl -fsS "$BASE/v1/accounts/$account/statement?limit=2&cursor=$cursor" | jq '.items | length')" = 1 ] || fail "second statement page"

[ "$(curl -fsS "$BASE/v1/ledger/trial-balance" | jq -r .balanced)" = true ] || fail "trial balance does not net to zero"

echo "Waiting for the round summary (outbox -> transport -> projection)"
for _ in $(seq 1 60); do
  round=$(curl -sS "$BASE/v1/rounds/$ROUND")
  if [ "$(echo "$round" | jq -r '.entries // 0')" = 2 ]; then break; fi
  sleep 1
done
[ "$(echo "$round" | jq -r .net.minor)" = -3500 ] || fail "round summary never arrived: $round"

echo "Smoke test passed for player $PLAYER"
