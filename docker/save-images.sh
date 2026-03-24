#!/usr/bin/env sh
# 文件：Docker 镜像离线导出脚本（save-images.sh） | File: Docker Save Images Script

set -eu

API_IMAGE_REPO="${API_IMAGE_REPO:-aura-api}"
AI_IMAGE_REPO="${AI_IMAGE_REPO:-aura-ai}"
IMAGE_TAG="${IMAGE_TAG:-}"
IMAGE_ARCHIVE_DIR="${IMAGE_ARCHIVE_DIR:-docker/dist}"

if [ -z "$IMAGE_TAG" ]; then
  echo "请先设置 IMAGE_TAG。"
  exit 1
fi

mkdir -p "$IMAGE_ARCHIVE_DIR"
API_IMAGE="${API_IMAGE_REPO}:${IMAGE_TAG}"
AI_IMAGE="${AI_IMAGE_REPO}:${IMAGE_TAG}"
ARCHIVE="${IMAGE_ARCHIVE_DIR}/aura-images-${IMAGE_TAG}.tar"

docker save -o "$ARCHIVE" "$API_IMAGE" "$AI_IMAGE"
echo "[RESULT] 导出完成：$ARCHIVE"
