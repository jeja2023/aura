#!/usr/bin/env sh
# File: Docker Load Images Script

set -eu

IMAGE_ARCHIVE_FILE="${IMAGE_ARCHIVE_FILE:-}"

if [ -z "$IMAGE_ARCHIVE_FILE" ]; then
  echo "Set IMAGE_ARCHIVE_FILE first."
  exit 1
fi

docker load -i "$IMAGE_ARCHIVE_FILE"
echo "[RESULT] Imported: $IMAGE_ARCHIVE_FILE"
