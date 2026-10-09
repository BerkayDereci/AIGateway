#!/usr/bin/env bash
# End-to-end demo smoke with curl (also the reference curl samples).
# Needs a running API in Development/Testing with Demo:Enabled=true. No real provider is called.
# Usage: scripts/smoke.sh [baseUrl] [imageFile]
set -euo pipefail
BASE=${1:-http://localhost:5080}
IMAGE=${2:-}
JAR=$(mktemp); trap 'rm -f "$JAR"' EXIT
EMAIL="smoke-$(date +%s)-$RANDOM@example.test"
PASS="Smoke-test-pass-$RANDOM"   # generated per run, never reused

j() { python3 -c "import json,sys; print(json.load(sys.stdin)$1)"; }
csrf() { curl -sf -b "$JAR" -c "$JAR" "$BASE/api/v1/auth/csrf" | j "['token']"; }
admin() { # method path [json]
  curl -sf -b "$JAR" -c "$JAR" -X "$1" "$BASE$2" -H "X-CSRF-TOKEN: $(csrf)" -H 'Content-Type: application/json' ${3:+-d "$3"}
}

echo "== health";        curl -sf "$BASE/health/ready"; echo
echo "== register";      admin POST /api/v1/auth/register "{\"email\":\"$EMAIL\",\"password\":\"$PASS\",\"workspaceName\":\"Smoke\"}" >/dev/null
WS=$(curl -sf -b "$JAR" "$BASE/api/v1/auth/me" | j "['workspaces'][0]['id']"); W=/api/v1/workspaces/$WS
echo "== project";       PROJECT=$(admin POST "$W/projects" '{"name":"Smoke"}' | j "['id']")
DEV=$(curl -sf -b "$JAR" "$BASE$W/projects/$PROJECT/environments" | j "[0]['id']")
echo "== profile";       PROFILE=$(admin POST "$W/profiles" '{"name":"workout-extraction"}' | j "['id']")
REV=$(admin POST "$W/profiles/$PROFILE/revisions" '{"config":{"requiredCapabilities":["text","image","jsonSchema"],"primary":{"provider":"demo","model":"demo-model","connectionId":null},"fallbacks":[],"outputTokenLimit":1000,"timeoutSeconds":60,"schemaMode":"native","pricingRequiredForBudgetEnforcement":true,"automaticRetryCount":0}}' | j "['id']")
admin PUT "$W/environments/$DEV/profiles/$PROFILE" "{\"revisionId\":\"$REV\"}" >/dev/null
echo "== api key";       KEY=$(admin POST "$W/environments/$DEV/keys" '{"name":"smoke"}' | j "['key']")
AUTH="Authorization: Bearer $KEY"
[ -n "${SMOKE_KEY_OUT:-}" ] && printf %s "$KEY" > "$SMOKE_KEY_OUT"  # for samples/dotnet; never echoed

echo "== text generation (idempotent)"
BODY='{"profile":"workout-extraction","messages":[{"role":"user","content":[{"type":"text","text":"Merhaba"}]}],"metadata":{"feature":"smoke"}}'
curl -sf "$BASE/api/v1/generations" -H "$AUTH" -H 'Content-Type: application/json' -H 'Idempotency-Key: smoke-1' -d "$BODY"; echo
curl -sfi "$BASE/api/v1/generations" -H "$AUTH" -H 'Content-Type: application/json' -H 'Idempotency-Key: smoke-1' -d "$BODY" | grep -i '^idempotent-replayed'

echo "== image upload + json_schema"
if [ -z "$IMAGE" ]; then IMAGE=$(mktemp).png; printf '\x89PNG\r\n\x1a\n\0\0\0\rIHDR\0\0\0\x01\0\0\0\x01\x08\x02\0\0\0\x90wS\xde\0\0\0\x0cIDATx\x9cc\xf8\xcf\xc0\0\0\x03\x01\x01\0\xc9\xfe\x92\xef\0\0\0\0IEND\xaeB`\x82' > "$IMAGE"; fi
ASSET=$(curl -sf "$BASE/api/v1/uploads" -H "$AUTH" -F "file=@$IMAGE" | j "['assetId']")
python3 - "$ASSET" <<'PY' > /tmp/aigw-smoke-request.json
import json, sys
r = json.load(open("examples/workout.request.json"))
r["messages"][1]["content"][1]["assetId"] = sys.argv[1]
print(json.dumps(r))
PY
curl -sf "$BASE/api/v1/generations" -H "$AUTH" -H 'Content-Type: application/json' -d @/tmp/aigw-smoke-request.json | j "['output']['json']"

echo "== SSE stream"
curl -sfN "$BASE/api/v1/generations" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"profile":"workout-extraction","stream":true,"messages":[{"role":"user","content":[{"type":"text","text":"akış"}]}]}' | grep -E '^event:' | sort | uniq -c

echo "== error contract (revoked key path)"
curl -s "$BASE/api/v1/generations" -H 'Authorization: Bearer pgw_invalid_invalid_invalid' -H 'Content-Type: application/json' -d "$BODY"; echo

# Optional paid check: runs only when the user supplies a key locally AND opts in. Never in CI.
if [ "${AIGW_ALLOW_PAID:-}" = "1" ] && [ -n "${AIGW_LIVE_KEY:-}" ] && [ -n "${AIGW_LIVE_PROVIDER:-}" ] && [ -n "${AIGW_LIVE_MODEL:-}" ]; then
  echo "== LIVE $AIGW_LIVE_PROVIDER/$AIGW_LIVE_MODEL (paid call)"
  CONN=$(admin POST "$W/provider-connections" "{\"provider\":\"$AIGW_LIVE_PROVIDER\",\"name\":\"live\",\"apiKey\":\"$AIGW_LIVE_KEY\"}" | j "['id']")
  LP=$(admin POST "$W/profiles" '{"name":"live"}' | j "['id']")
  LR=$(admin POST "$W/profiles/$LP/revisions" "{\"config\":{\"requiredCapabilities\":[\"text\"],\"primary\":{\"provider\":\"$AIGW_LIVE_PROVIDER\",\"model\":\"$AIGW_LIVE_MODEL\",\"connectionId\":\"$CONN\"},\"fallbacks\":[],\"outputTokenLimit\":64,\"timeoutSeconds\":60,\"schemaMode\":\"native\",\"pricingRequiredForBudgetEnforcement\":false,\"automaticRetryCount\":0}}" | j "['id']")
  admin PUT "$W/environments/$DEV/profiles/$LP" "{\"revisionId\":\"$LR\"}" >/dev/null
  curl -s "$BASE/api/v1/generations" -H "$AUTH" -H 'Content-Type: application/json' \
    -d '{"profile":"live","messages":[{"role":"user","content":[{"type":"text","text":"Reply with OK."}]}]}'; echo
fi

echo "== usage"
curl -sf -b "$JAR" "$BASE$W/usage" | j "['summary']"
echo "SMOKE OK"
