#!/usr/bin/env sh
# 文件：Docker 联调启动脚本（up-full.sh） | File: Docker Full Up Script

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.full.example.yml"
TEMPLATE_FILE="$ROOT_DIR/docker/.env.full.example"

if [ ! -f "$ENV_FILE" ]; then
  echo "未找到 $ENV_FILE。请先复制 $TEMPLATE_FILE 为 .env 并填写真实变量。"
  exit 1
fi

echo "使用环境文件: $ENV_FILE"
echo "启动编排文件: $COMPOSE_FILE"
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build
echo ""
echo "启动完成。业务数据持久化：compose 命名卷（含 aura-api-storage → /app/storage）在 down 时默认保留。"
echo "可执行以下命令查看状态："
echo "docker compose -f \"$COMPOSE_FILE\" ps"
