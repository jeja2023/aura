#!/usr/bin/env sh
# 文件：Docker 联调停止脚本（down-full.sh） | File: Docker Full Down Script
# 用法：默认保留命名卷。需同时删卷：sh ./docker/down-full.sh --volumes

set -eu

ROOT_DIR="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.full.example.yml"

REMOVE_VOLUMES=false
for arg in "$@"; do
  case "$arg" in
    -v|--volumes) REMOVE_VOLUMES=true ;;
  esac
done

if [ "$REMOVE_VOLUMES" = "true" ]; then
  echo "停止并删除容器及关联卷（含数据库与 aura-api-storage 等，慎用）..."
  docker compose -f "$COMPOSE_FILE" down -v
else
  echo "停止并清理容器网络（保留命名卷：数据库与 /app/storage 等数据仍在）..."
  docker compose -f "$COMPOSE_FILE" down
fi
echo "已停止。"
