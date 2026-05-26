#!/usr/bin/env sh
# File: Docker Offline Package Script

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env.docker"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.yml"

if [ ! -f "$ENV_FILE" ]; then
  echo "Missing $ENV_FILE. Copy docker/.env.docker.example to .env.docker first."
  exit 1
fi

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

TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
PACKAGE_ROOT="$ROOT_DIR/docker/dist/aura-offline-$TIMESTAMP"
mkdir -p "$PACKAGE_ROOT"
IMAGES_ARCHIVE="$PACKAGE_ROOT/aura-images.tar"

IMAGES="$(read_env POSTGRES_IMAGE postgres:16-alpine) $(read_env REDIS_IMAGE redis:7-alpine) $(read_env ARANGO_IMAGE arangodb:3.12) $(read_env API_IMAGE aura-api:local) $(read_env AI_IMAGE aura-ai:local)"

echo "==> Exporting images"
# shellcheck disable=SC2086
docker save -o "$IMAGES_ARCHIVE" $IMAGES

echo "==> Copying deployment files"
cp "$ENV_FILE" "$PACKAGE_ROOT/.env.docker"
cp "$COMPOSE_FILE" "$PACKAGE_ROOT/docker-compose.yml"
cp -R "$ROOT_DIR/database" "$PACKAGE_ROOT/database"
cp -R "$ROOT_DIR/frontend" "$PACKAGE_ROOT/frontend"
if [ -d "$ROOT_DIR/models" ]; then
  cp -R "$ROOT_DIR/models" "$PACKAGE_ROOT/models"
fi

cat > "$PACKAGE_ROOT/README.txt" <<'EOF'
Aura offline update package

1. Load images:
   docker load -i aura-images.tar

2. Review .env.docker and keep IMAGE_PULL_POLICY=never on the disconnected server.

3. Start or update disconnected deployment:
   docker compose --env-file .env.docker -f docker-compose.yml up -d --no-build

4. Stop while keeping data:
   docker compose --env-file .env.docker -f docker-compose.yml down

5. Stop and remove data volumes only when intentionally wiping the environment:
   docker compose --env-file .env.docker -f docker-compose.yml down -v
EOF

echo "[RESULT] Offline package created: $PACKAGE_ROOT"
