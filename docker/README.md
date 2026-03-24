# Docker 目录说明

本目录用于集中管理项目 Docker 化相关文件，避免与业务代码混放。

## 当前示例

- `docker-compose.ops-check.example.yml`
  - 用途：执行上线就绪巡检脚本 `上线就绪检查脚本.ps1`
  - 适用：本地联调、CI 发布前 Gate 检查
- `docker-compose.full.example.yml`
  - 用途：本地一键联调（后端 + AI + MySQL + Redis）
  - 适用：开发环境容器化验证
- `docker-compose.prod.template.yml`
  - 用途：生产部署模板（仅占位，不包含明文密钥）
  - 适用：生产编排落地前的变量清单对齐
- `.env.full.example`
  - 用途：`docker-compose.full.example.yml` 的变量模板
- `.env.prod.example`
  - 用途：`docker-compose.prod.template.yml` 的变量模板
- `backend.Dockerfile`
  - 用途：构建后端 API 运行镜像（多阶段构建）
- `ai.Dockerfile`
  - 用途：构建 AI 服务运行镜像
- `up-full.ps1` / `down-full.ps1`
  - 用途：Windows 下启动/停止 full 联调编排
- `up-full.sh` / `down-full.sh`
  - 用途：Linux/macOS 下启动/停止 full 联调编排
- `check-full.ps1` / `check-full.sh`
  - 用途：full 联调启动后的 API/AI 健康检查
- `build-images.ps1` / `build-images.sh`
  - 用途：构建 `api/ai` 镜像并打业务标签
- `push-images.ps1` / `push-images.sh`
  - 用途：将业务镜像推送到私有仓库
- `login-registry.ps1` / `login-registry.sh`
  - 用途：登录私有镜像仓库
- `save-images.ps1` / `save-images.sh`
  - 用途：导出离线镜像包（tar）
- `load-images.ps1` / `load-images.sh`
  - 用途：导入离线镜像包（tar）
- `.env.registry.example`
  - 用途：私有仓库与离线分发变量模板
- `.github/workflows/docker-build-push.example.yml`
  - 用途：GitHub Actions 镜像构建与推送模板
- `Jenkinsfile.docker.example`
  - 用途：Jenkins 镜像构建与推送模板

## 快速使用

1. 在项目根目录准备环境变量文件：
   - 复制 `.env.example` 为 `.env`
   - 填写 `AURA_ADMIN_USER`、`AURA_ADMIN_PASSWORD`
2. 在项目根目录执行：
   - `docker compose -f docker/docker-compose.ops-check.example.yml run --rm ops-check`

## Full 示例使用

1. 在项目根目录准备联调环境变量：
   - 复制 `docker/.env.full.example` 为 `.env`
   - 按需修改 MySQL/JWT/HMAC/管理员密码等变量
2. 在项目根目录执行：
   - `docker compose --env-file .env -f docker/docker-compose.full.example.yml up -d`
   - 或（Windows）：`powershell -ExecutionPolicy Bypass -File .\docker\up-full.ps1`
   - 或（Linux/macOS）：`sh ./docker/up-full.sh`
3. 查看运行状态：
   - `docker compose -f docker/docker-compose.full.example.yml ps`
4. 停止并清理：
   - `docker compose -f docker/docker-compose.full.example.yml down`
   - 或（Windows）：`powershell -ExecutionPolicy Bypass -File .\docker\down-full.ps1`
   - 或（Linux/macOS）：`sh ./docker/down-full.sh`
5. 健康检查：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\check-full.ps1`
   - Linux/macOS：`sh ./docker/check-full.sh`

## 企业网络适配

1. 在 `.env`（基于 `docker/.env.full.example`）中设置基础镜像变量：
   - `MYSQL_IMAGE`、`REDIS_IMAGE`、`PYTHON_BASE_IMAGE`
   - `DOTNET_SDK_IMAGE`、`DOTNET_ASPNET_IMAGE`
2. 若内网有镜像代理/私服，将上述变量改为内网地址（例如 `registry.local/library/python:3.11-slim`）。
3. 若需要发布到私有仓库，设置：
   - `API_IMAGE_REPO`（示例：`registry.local/aura/aura-api`）
   - `AI_IMAGE_REPO`（示例：`registry.local/aura/aura-ai`）
   - `IMAGE_TAG`（示例：`v0.0.6`）
4. 构建并打标签：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\build-images.ps1`
   - Linux/macOS：`sh ./docker/build-images.sh`
5. 推送私有仓库：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\push-images.ps1`
   - Linux/macOS：`sh ./docker/push-images.sh`

## 私有仓库登录

1. 复制 `docker/.env.registry.example`，按实际环境设置变量：
   - `REGISTRY_HOST`、`REGISTRY_USER`、`REGISTRY_PASSWORD`
2. 登录仓库：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\login-registry.ps1`
   - Linux/macOS：`sh ./docker/login-registry.sh`

## CI/CD 模板

- GitHub Actions：
  - 模板文件：`.github/workflows/docker-build-push.example.yml`
  - 使用前请在仓库 Secrets 中配置：`REGISTRY_HOST`、`REGISTRY_USER`、`REGISTRY_PASSWORD`、`API_IMAGE_REPO`、`AI_IMAGE_REPO` 以及 `.env.full.example` 中所需变量。
- Jenkins：
  - 模板文件：`docker/Jenkinsfile.docker.example`
  - 使用前请在 Jenkins Credentials 中配置同名变量，并根据实际 Agent（Windows/Linux）调整命令执行器。

## 离线镜像迁移

### 发送端（可联网构建机）

1. 设置 `API_IMAGE_REPO`、`AI_IMAGE_REPO`、`IMAGE_TAG`
2. 导出镜像包：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\save-images.ps1`
   - Linux/macOS：`sh ./docker/save-images.sh`
3. 将 tar 包拷贝到目标环境（U 盘/内网文件传输）。

### 接收端（离线部署机）

1. 设置 `IMAGE_ARCHIVE_FILE` 指向 tar 包路径
2. 导入镜像包：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\load-images.ps1`
   - Linux/macOS：`sh ./docker/load-images.sh`
3. 在部署编排中将 `API_IMAGE`、`AI_IMAGE` 指向已导入镜像标签并启动。

## 安全建议

- `.env` 仅用于本地开发，不要提交到仓库。
- 生产环境优先使用 CI/CD Secret 或容器编排 Secret（例如 Kubernetes Secret）。
- 禁止把真实密码写入镜像、代码仓库和公开日志。

## 生产模板说明

- `docker-compose.prod.template.yml` 是占位模板，不可直接用于生产。
- 生产环境应通过 CI/CD Secret、Kubernetes Secret 或云密钥管理服务注入变量。
- 建议将 `backend.Dockerfile`、`ai.Dockerfile` 产物推送到私有镜像仓库，再由生产编排引用固定版本镜像。
- 可先复制 `docker/.env.prod.example` 为本地变量文件进行校验，再按实际环境改为 Secret 注入。
- `docker-compose.prod.template.yml` 已支持 `API_IMAGE`、`AI_IMAGE` 变量，可直接对接私有仓库镜像。
