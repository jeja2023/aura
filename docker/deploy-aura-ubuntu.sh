#!/usr/bin/env bash
# 文件：Ubuntu 一键部署脚本 | File: Ubuntu One-Click Deploy Script
# 用途：在 Ubuntu 云服务器上完成 Aura 项目的拉取、配置、构建与启动。

set -euo pipefail

########################################
# 0) 按需修改以下变量
########################################
GIT_REPO_URL="https://github.com/jeja2023/aura.git"
DEPLOY_DIR="/opt/aura"
BRANCH="main"

# 管理员账号
AURA_ADMIN_USER="admin"
AURA_ADMIN_PASSWORD="admin123"

# MySQL 配置
MYSQL_ROOT_PASSWORD="root_123456"
MYSQL_DATABASE="aura"
MYSQL_USER="dev_user"
MYSQL_PASSWORD="aura_123456"

# 安全密钥（建议 32 位以上随机字符串）
JWT_KEY="Xnewq6I_LyhsnOqbbX-ntVw1duomeBnWcM-xZC1CYPu6ZrJ9EQ0i8rvObqC-g3C8"
HMAC_SECRET="N7u_gxz_PsdZtnkYXVzzb3AlQ0brgoJWTM99WcuZE3yOWEefaQNl9Y6NUJBv7-rz"

# AI 配置（通常保持默认即可）
MILVUS_URI="http://127.0.0.1:19530"
AURA_MODEL_PATH="/app/models/osnet_ibn_x1_0.onnx"

########################################
# 1) 安装基础依赖与 Docker
########################################
echo "==> 安装基础依赖..."
sudo apt update
sudo apt install -y git curl ca-certificates

if ! command -v docker >/dev/null 2>&1; then
  echo "==> 安装 Docker..."
  curl -fsSL https://get.docker.com | sudo sh
fi

# 将当前用户加入 docker 用户组，避免每次都使用 sudo docker
sudo usermod -aG docker "$USER" || true

########################################
# 2) 拉取或更新代码
########################################
echo "==> 准备部署目录: ${DEPLOY_DIR}"
sudo mkdir -p "$DEPLOY_DIR"
sudo chown -R "$USER:$USER" "$DEPLOY_DIR"

if [ ! -d "${DEPLOY_DIR}/.git" ]; then
  echo "==> 首次拉取仓库..."
  git clone -b "$BRANCH" "$GIT_REPO_URL" "$DEPLOY_DIR"
else
  echo "==> 仓库已存在，拉取最新代码..."
  git -C "$DEPLOY_DIR" fetch origin
  git -C "$DEPLOY_DIR" checkout "$BRANCH"
  git -C "$DEPLOY_DIR" pull --ff-only origin "$BRANCH"
fi

cd "$DEPLOY_DIR"

########################################
# 3) 生成运行环境变量文件 .env（存在则跳过）
########################################
if [ -f .env ]; then
  echo "==> 检测到已存在 .env，跳过自动生成并保留原配置"
else
  echo "==> 未检测到 .env，自动生成配置文件"
  cat > .env <<EOF
AURA_ADMIN_USER=${AURA_ADMIN_USER}
AURA_ADMIN_PASSWORD=${AURA_ADMIN_PASSWORD}

MYSQL_IMAGE=mysql:8.0
REDIS_IMAGE=redis:7-alpine
PYTHON_BASE_IMAGE=python:3.11-slim
DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview
DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-preview

MYSQL_ROOT_PASSWORD=${MYSQL_ROOT_PASSWORD}
MYSQL_DATABASE=${MYSQL_DATABASE}
MYSQL_USER=${MYSQL_USER}
MYSQL_PASSWORD=${MYSQL_PASSWORD}

JWT_KEY=${JWT_KEY}
HMAC_SECRET=${HMAC_SECRET}

MILVUS_URI=${MILVUS_URI}
AURA_MODEL_PATH=${AURA_MODEL_PATH}
EOF
fi

########################################
# 4) 模型文件检查（AI 服务依赖）
########################################
if [ ! -f "${DEPLOY_DIR}/models/osnet_ibn_x1_0.onnx" ]; then
  echo "WARN: 未找到模型文件: ${DEPLOY_DIR}/models/osnet_ibn_x1_0.onnx"
  echo "WARN: AI 服务可能启动但 model_loaded=false，请先上传模型文件。"
fi

########################################
# 5) 启动容器服务
########################################
echo "==> 启动容器服务..."
docker compose --env-file .env -f docker/docker-compose.full.example.yml up -d --build

echo "==> 当前容器状态"
docker compose -f docker/docker-compose.full.example.yml ps

########################################
# 6) 健康检查
########################################
echo "==> 等待服务就绪..."
sleep 8

echo "==> AI 健康检查"
curl -fsS http://127.0.0.1:8000/ || true
echo

echo "==> API 健康检查"
curl -fsS http://127.0.0.1:5000/api/health || true
echo

########################################
# 7) 可选：放行防火墙端口
########################################
if command -v ufw >/dev/null 2>&1; then
  echo "==> 配置防火墙端口（可选）"
  sudo ufw allow 5000/tcp || true
  sudo ufw allow 8000/tcp || true
fi

echo "==> 部署完成。常用命令："
echo "查看日志: docker compose -f docker/docker-compose.full.example.yml logs -f api ai"
echo "停止服务: docker compose -f docker/docker-compose.full.example.yml down"
echo "更新重启: git -C ${DEPLOY_DIR} pull && docker compose --env-file .env -f docker/docker-compose.full.example.yml up -d --build"
