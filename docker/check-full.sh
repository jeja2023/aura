#!/usr/bin/env sh
# 文件：Docker 联调健康检查脚本（check-full.sh） | File: Docker Full Health Check Script

set -eu

echo "1) 检查 AI 存活..."
AI_LIVE_JSON="$(curl -fsS http://127.0.0.1:8000/live)"
echo "$AI_LIVE_JSON" | grep '"code":[[:space:]]*0' >/dev/null
echo "   AI 进程存活。"

echo "2) 检查 AI 就绪..."
AI_JSON="$(curl -fsS http://127.0.0.1:8000/ready)"
echo "$AI_JSON" | grep '"code":[[:space:]]*0' >/dev/null
echo "$AI_JSON" | grep '"model_loaded":[[:space:]]*true' >/dev/null
echo "   AI 正常。"

echo "3) 检查 API 健康..."
API_JSON="$(curl -fsS http://127.0.0.1:5000/api/health)"
echo "$API_JSON" | grep '"code":[[:space:]]*0' >/dev/null
echo "   API 正常。"

echo "[RESULT] FULL STACK HEALTHY"
