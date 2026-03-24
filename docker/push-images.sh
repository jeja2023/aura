#!/usr/bin/env sh
# 文件：Docker 镜像推送脚本（push-images.sh） | File: Docker Push Images Script

set -eu

API_IMAGE_REPO="${API_IMAGE_REPO:-aura-api}"
AI_IMAGE_REPO="${AI_IMAGE_REPO:-aura-ai}"
IMAGE_TAG="${IMAGE_TAG:-}"

if [ -z "$IMAGE_TAG" ]; then
  echo "请先设置 IMAGE_TAG。"
  exit 1
fi

API_IMAGE="${API_IMAGE_REPO}:${IMAGE_TAG}"
AI_IMAGE="${AI_IMAGE_REPO}:${IMAGE_TAG}"

echo "推送镜像：$API_IMAGE"
docker push "$API_IMAGE"

echo "推送镜像：$AI_IMAGE"
docker push "$AI_IMAGE"

echo "[RESULT] 镜像推送完成。"
