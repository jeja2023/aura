#!/usr/bin/env sh
# File: Docker Up Script

set -eu

BUILD=false
for arg in "$@"; do
  case "$arg" in
    --build) BUILD=true ;;
  esac
done

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env.docker"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.yml"
TEMPLATE_FILE="$ROOT_DIR/.env.docker.example"

if [ ! -f "$ENV_FILE" ]; then
  echo "Missing $ENV_FILE. Copy $TEMPLATE_FILE to .env.docker, then fill image tags and secrets."
  exit 1
fi

compose() {
  if command -v docker-compose >/dev/null 2>&1; then
    docker-compose "$@"
  else
    docker compose "$@"
  fi
}

echo "Env file: $ENV_FILE"
echo "Compose file: $COMPOSE_FILE"
set -- --env-file "$ENV_FILE" -f "$COMPOSE_FILE"
if [ "$BUILD" = "true" ]; then
  compose "$@" up -d --build
else
  compose "$@" up -d --no-build
fi
echo ""
if [ "$BUILD" = "true" ]; then
  echo "Started. Images were built/pulled as needed. For offline restarts or updates, use uploaded images and run without --build."
else
  echo "Started. This stack uses existing images by default. Run with --build only during online bootstrap or local rebuilds."
fi
echo "Status command:"
echo "docker compose --env-file \"$ENV_FILE\" -f \"$COMPOSE_FILE\" ps"
