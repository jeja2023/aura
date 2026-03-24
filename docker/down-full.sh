#!/usr/bin/env sh
# 文件：Docker 联调停止脚本（down-full.sh） | File: Docker Full Down Script

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.full.example.yml"

echo "停止并清理容器网络（保留数据卷）..."
docker compose -f "$COMPOSE_FILE" down
echo "已停止。"
