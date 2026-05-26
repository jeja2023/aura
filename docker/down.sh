#!/usr/bin/env sh
# File: Docker Down Script
# Usage: keep named volumes by default. Remove volumes too with: sh ./docker/down.sh --volumes

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env.docker"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.yml"

compose() {
  if command -v docker-compose >/dev/null 2>&1; then
    docker-compose "$@"
  else
    docker compose "$@"
  fi
}

REMOVE_VOLUMES=false
for arg in "$@"; do
  case "$arg" in
    -v|--volumes) REMOVE_VOLUMES=true ;;
  esac
done

COMPOSE_ARGS="--env-file $ENV_FILE -f $COMPOSE_FILE"

if [ "$REMOVE_VOLUMES" = "true" ]; then
  echo "Stopping containers and removing named volumes, including PostgreSQL/Redis/ArangoDB data..."
  # shellcheck disable=SC2086
  compose $COMPOSE_ARGS down -v
else
  echo "Stopping containers and keeping named volumes..."
  # shellcheck disable=SC2086
  compose $COMPOSE_ARGS down
fi
echo "Stopped."
