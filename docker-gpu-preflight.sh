#!/usr/bin/env sh
# File: Docker GPU network preflight
set -eu

NETWORK_NAME="gpu-bridge"
CREATE=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --network)
      shift
      NETWORK_NAME="${1:?missing network name}"
      ;;
    --create)
      CREATE=true
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 64
      ;;
  esac
  shift
done

if ! command -v docker >/dev/null 2>&1; then
  echo "docker command was not found. Install Docker and start the Docker service first." >&2
  exit 127
fi

if docker network inspect "$NETWORK_NAME" >/dev/null 2>&1; then
  echo "Docker network '$NETWORK_NAME' exists."
  exit 0
fi

if [ "$CREATE" != "true" ]; then
  echo "Docker network '$NETWORK_NAME' does not exist." >&2
  echo "Create it with: sh ./docker-gpu-preflight.sh --create" >&2
  exit 2
fi

docker network create "$NETWORK_NAME" >/dev/null
echo "Created Docker network '$NETWORK_NAME'."
