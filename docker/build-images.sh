#!/usr/bin/env sh
# 文件：Docker 镜像构建脚本（build-images.sh） | File: Docker Build Images Script

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.full.example.yml"
TEMPLATE_FILE="$ROOT_DIR/docker/.env.full.example"

if [ ! -f "$ENV_FILE" ]; then
  echo "未找到 $ENV_FILE。请先复制 $TEMPLATE_FILE 为 .env 并填写变量。"
  exit 1
fi

echo "使用环境文件: $ENV_FILE"
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" build ai api

API_IMAGE_REPO="${API_IMAGE_REPO:-aura-api}"
AI_IMAGE_REPO="${AI_IMAGE_REPO:-aura-ai}"
IMAGE_TAG="${IMAGE_TAG:-$(date +%Y%m%d-%H%M%S)}"

docker tag aura-api:local "${API_IMAGE_REPO}:${IMAGE_TAG}"
docker tag aura-ai:local "${AI_IMAGE_REPO}:${IMAGE_TAG}"

echo ""
echo "构建并打标签完成："
echo "  ${API_IMAGE_REPO}:${IMAGE_TAG}"
echo "  ${AI_IMAGE_REPO}:${IMAGE_TAG}"
