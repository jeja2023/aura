#!/usr/bin/env bash
# 文件：Ubuntu 一键部署脚本 | File: Ubuntu One-Click Deploy Script
# 用途：在 Ubuntu 云服务器上完成 Aura 项目的拉取、配置、构建与启动。

set -euo pipefail

# 记录脚本总耗时，便于定位慢点
SCRIPT_START_TS="$(date +%s)"
function print_elapsed() {
  local now
  now="$(date +%s)"
  echo "==> 当前累计耗时: $((now - SCRIPT_START_TS)) 秒"
}

########################################
# 0) 按需修改以下变量
########################################
GIT_REPO_URL="https://github.com/jeja2023/aura.git"
DEPLOY_DIR="/opt/aura"
BRANCH="main"

# 管理员账号
AURA_ADMIN_USER="admin"
AURA_ADMIN_PASSWORD="admin123"

# PostgreSQL 配置
POSTGRES_DB="aura"
POSTGRES_USER="postgres"
POSTGRES_PASSWORD="aura_123456"

# 安全密钥（建议 32 位以上随机字符串）
JWT_KEY="Xnewq6I_LyhsnOqbbX-ntVw1duomeBnWcM-xZC1CYPu6ZrJ9EQ0i8rvObqC-g3C8"
HMAC_SECRET="N7u_gxz_PsdZtnkYXVzzb3AlQ0brgoJWTM99WcuZE3yOWEefaQNl9Y6NUJBv7-rz"

# AI 配置（通常保持默认即可）
ARANGO_URI="http://arangodb:8529"
ARANGO_DB="aura"
ARANGO_USER="_system"
ARANGO_ROOT_PASSWORD="arangodb_123456"
ARANGO_PASSWORD="arangodb_123456"
AURA_MODEL_PATH="/app/models/osnet_ibn_x1_0.onnx"

# API 运行环境（与 appsettings.Production.json、前端静态路径一致；写入 .env 供 compose 使用）
ASPNETCORE_ENVIRONMENT_VALUE="Production"

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

POSTGRES_IMAGE=postgres:16-alpine
REDIS_IMAGE=redis:7-alpine
PYTHON_BASE_IMAGE=python:3.11-slim
DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview
DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-preview

POSTGRES_DB=${POSTGRES_DB}
POSTGRES_USER=${POSTGRES_USER}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD}

JWT_KEY=${JWT_KEY}
HMAC_SECRET=${HMAC_SECRET}

ARANGO_URI=${ARANGO_URI}
ARANGO_DB=${ARANGO_DB}
ARANGO_USER=${ARANGO_USER}
ARANGO_ROOT_PASSWORD=${ARANGO_ROOT_PASSWORD}
ARANGO_PASSWORD=${ARANGO_PASSWORD}
AURA_MODEL_PATH=${AURA_MODEL_PATH}

ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT_VALUE}
EOF
fi

# 升级后若沿用旧 .env，补全 ASPNETCORE_ENVIRONMENT（与 appsettings.Production.json、/app/frontend 挂载一致）
if ! grep -q '^ASPNETCORE_ENVIRONMENT=' .env 2>/dev/null; then
  echo "ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT_VALUE}" >> .env
  echo "==> 已向 .env 追加 ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT_VALUE}（旧部署补全）"
fi

# 读取 .env 并导出，统一用于后续 compose 与变量校验
set -a
source .env
set +a

# 关键变量校验，避免变量为空导致 compose 异常
required_vars=(
  POSTGRES_IMAGE
  REDIS_IMAGE
  PYTHON_BASE_IMAGE
  DOTNET_SDK_IMAGE
  DOTNET_ASPNET_IMAGE
  POSTGRES_DB
  POSTGRES_USER
  POSTGRES_PASSWORD
  JWT_KEY
  HMAC_SECRET
  AURA_ADMIN_USER
  AURA_ADMIN_PASSWORD
  ARANGO_URI
  ARANGO_DB
  ARANGO_USER
  ARANGO_ROOT_PASSWORD
  ARANGO_PASSWORD
  AURA_MODEL_PATH
)
for v in "${required_vars[@]}"; do
  if [ -z "${!v:-}" ]; then
    echo "ERROR: .env 中缺少必填变量: ${v}"
    exit 1
  fi
done

echo "==> .env 关键变量检查通过"
print_elapsed

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
print_elapsed

echo "==> 当前容器状态"
docker compose --env-file .env -f docker/docker-compose.full.example.yml ps

########################################
# 6) 健康检查
########################################
echo "==> 等待服务就绪..."
sleep 8

echo "==> AI 健康检查"
curl --max-time 10 -fsS http://127.0.0.1:8000/ || true
echo

echo "==> API 健康检查"
curl --max-time 10 -fsS http://127.0.0.1:5000/api/health || true
echo

echo "==> API 首页（frontend 静态挂载）检查"
HTTP_INDEX="$(curl -sS --max-time 10 -o /dev/null -w "%{http_code}" http://127.0.0.1:5000/index/ || echo "000")"
echo "HTTP ${HTTP_INDEX}"
if [ "${HTTP_INDEX}" != "200" ]; then
  echo "WARN: 首页未返回 200，请确认仓库含 frontend 目录且 compose 已挂载 ../frontend -> /app/frontend"
fi
print_elapsed

########################################
# 7) 可选：放行防火墙端口
########################################
if command -v ufw >/dev/null 2>&1; then
  echo "==> 配置防火墙端口（可选）"
  sudo ufw allow 5000/tcp || true
  sudo ufw allow 8000/tcp || true
fi

echo "==> 部署完成。常用命令："
echo "查看日志: docker compose --env-file .env -f docker/docker-compose.full.example.yml logs -f api ai"
echo "停止服务: docker compose --env-file .env -f docker/docker-compose.full.example.yml down"
echo "更新重启: git -C ${DEPLOY_DIR} pull && docker compose --env-file .env -f docker/docker-compose.full.example.yml up -d --build"
if grep -q '^ASPNETCORE_ENVIRONMENT=' .env 2>/dev/null; then
  echo "提示: 当前 .env 中 $(grep '^ASPNETCORE_ENVIRONMENT=' .env | head -1)"
else
  echo "WARN: .env 未找到 ASPNETCORE_ENVIRONMENT，请手动设为 Production"
fi
echo "提示: 若构建时间超过 20 分钟，可执行日志命令判断是否网络拉镜像过慢。"
