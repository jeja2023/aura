#!/usr/bin/env sh
# File: Docker Build Images Script

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env.docker"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.yml"
TEMPLATE_FILE="$ROOT_DIR/docker/.env.docker.example"

if [ ! -f "$ENV_FILE" ]; then
  echo "Missing $ENV_FILE. Copy $TEMPLATE_FILE to .env.docker, then fill image tags and build base images."
  exit 1
fi

compose() {
  if command -v docker-compose >/dev/null 2>&1; then
    docker-compose "$@"
  else
    docker compose "$@"
  fi
}

read_env() {
  name="$1"
  default_value="$2"
  value="$(awk -F= -v key="$name" '$1 == key { sub(/^[^=]*=/, ""); print; exit }' "$ENV_FILE")"
  if [ -z "$value" ]; then
    printf '%s' "$default_value"
  else
    printf '%s' "$value"
  fi
}

echo "Env file: $ENV_FILE"
compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" build ai api

BUILT_API_IMAGE="$(read_env API_IMAGE aura-api:local)"
BUILT_AI_IMAGE="$(read_env AI_IMAGE aura-ai:local)"
API_IMAGE_REPO="${API_IMAGE_REPO:-aura-api}"
AI_IMAGE_REPO="${AI_IMAGE_REPO:-aura-ai}"
IMAGE_TAG="${IMAGE_TAG:-$(date +%Y%m%d-%H%M%S)}"

docker tag "$BUILT_API_IMAGE" "${API_IMAGE_REPO}:${IMAGE_TAG}"
docker tag "$BUILT_AI_IMAGE" "${AI_IMAGE_REPO}:${IMAGE_TAG}"

echo ""
echo "Built and tagged:"
echo "  ${API_IMAGE_REPO}:${IMAGE_TAG}"
echo "  ${AI_IMAGE_REPO}:${IMAGE_TAG}"
