#!/usr/bin/env sh
# File: Docker Registry Login Script

set -eu

if [ -z "${REGISTRY_HOST:-}" ]; then
  echo "Set REGISTRY_HOST first."
  exit 1
fi
if [ -z "${REGISTRY_USER:-}" ]; then
  echo "Set REGISTRY_USER first."
  exit 1
fi
if [ -z "${REGISTRY_PASSWORD:-}" ]; then
  echo "Set REGISTRY_PASSWORD first."
  exit 1
fi

echo "$REGISTRY_PASSWORD" | docker login "$REGISTRY_HOST" -u "$REGISTRY_USER" --password-stdin
echo "[RESULT] Logged in: $REGISTRY_HOST"
