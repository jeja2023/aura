#!/usr/bin/env sh
# 文件：Docker 仓库登录脚本（login-registry.sh） | File: Docker Registry Login Script

set -eu

if [ -z "${REGISTRY_HOST:-}" ]; then
  echo "请先设置 REGISTRY_HOST。"
  exit 1
fi
if [ -z "${REGISTRY_USER:-}" ]; then
  echo "请先设置 REGISTRY_USER。"
  exit 1
fi
if [ -z "${REGISTRY_PASSWORD:-}" ]; then
  echo "请先设置 REGISTRY_PASSWORD。"
  exit 1
fi

echo "$REGISTRY_PASSWORD" | docker login "$REGISTRY_HOST" -u "$REGISTRY_USER" --password-stdin
echo "[RESULT] 已登录仓库：$REGISTRY_HOST"
