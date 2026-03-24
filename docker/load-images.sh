#!/usr/bin/env sh
# 文件：Docker 镜像离线导入脚本（load-images.sh） | File: Docker Load Images Script

set -eu

IMAGE_ARCHIVE_FILE="${IMAGE_ARCHIVE_FILE:-}"
if [ -z "$IMAGE_ARCHIVE_FILE" ]; then
  echo "请先设置 IMAGE_ARCHIVE_FILE（tar 包路径）。"
  exit 1
fi
if [ ! -f "$IMAGE_ARCHIVE_FILE" ]; then
  echo "未找到镜像包：$IMAGE_ARCHIVE_FILE"
  exit 1
fi

docker load -i "$IMAGE_ARCHIVE_FILE"
echo "[RESULT] 导入完成：$IMAGE_ARCHIVE_FILE"
