#!/usr/bin/env sh
# File: Docker Health Check Script

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env.docker"

read_env() {
  name="$1"
  default_value="$2"
  if [ ! -f "$ENV_FILE" ]; then
    printf '%s' "$default_value"
    return
  fi
  value="$(awk -F= -v key="$name" '$1 == key { sub(/^[^=]*=/, ""); print; exit }' "$ENV_FILE")"
  if [ -z "$value" ]; then
    printf '%s' "$default_value"
  else
    printf '%s' "$value"
  fi
}

API_PORT="$(read_env API_PORT 5000)"
AI_PORT="$(read_env AI_PORT 8000)"

echo "1) Checking AI live endpoint..."
AI_LIVE_JSON="$(curl -fsS "http://127.0.0.1:$AI_PORT/live")"
echo "$AI_LIVE_JSON" | grep '"code":[[:space:]]*0' >/dev/null
echo "   AI process is live."

echo "2) Checking AI readiness..."
AI_JSON="$(curl -fsS "http://127.0.0.1:$AI_PORT/ready")"
echo "$AI_JSON" | grep '"code":[[:space:]]*0' >/dev/null
echo "$AI_JSON" | grep '"model_loaded":[[:space:]]*true' >/dev/null
echo "   AI ready."

echo "3) Checking API health..."
API_JSON="$(curl -fsS "http://127.0.0.1:$API_PORT/api/health")"
echo "$API_JSON" | grep '"code":[[:space:]]*0' >/dev/null
echo "   API healthy."

echo "[RESULT] DOCKER STACK HEALTHY"
