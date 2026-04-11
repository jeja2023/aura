# Docker 目录说明

本目录用于集中管理项目 Docker 化相关文件，避免与业务代码混放。

与仓库根目录的 `.env.example`（通用环境变量模板）配合使用；Full 联调另有 `docker/.env.full.example`（含容器内 `Host=pgsql` 等编排专用项）。

## 当前示例

- `docker-compose.ops-check.example.yml`
  - 用途：执行上线就绪巡检脚本 `上线就绪检查脚本.ps1`
  - 适用：本地联调、CI 发布前 Gate 检查
- `docker-compose.full.example.yml`
  - 用途：本地一键联调（后端 + AI + PostgreSQL + Redis + ArangoDB）
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
- `deploy-aura-ubuntu.sh`
  - 用途：Ubuntu 服务器上拉取代码、生成 `.env`、启动 `docker-compose.full.example.yml` 的一键脚本

## 快速使用（上线就绪巡检）

1. 在项目根目录准备 `.env`：可复制仓库根目录的 **`.env.example`**（与根目录 `README.md` 说明一致），或复制 `docker/.env.full.example`。至少填写：
   - `AURA_ADMIN_USER`、`AURA_ADMIN_PASSWORD`（与待巡检环境一致）
2. 执行：
   - `docker compose --env-file .env -f docker/docker-compose.ops-check.example.yml run --rm ops-check`

## Full 示例使用

1. 在项目根目录准备联调环境变量：
   - 复制 `docker/.env.full.example` 为 `.env`
  - 按需修改 PostgreSQL/JWT/HMAC/管理员密码等变量
2. 在项目根目录执行：
   - `docker compose --env-file .env -f docker/docker-compose.full.example.yml up -d`
   - 或（Windows）：`powershell -ExecutionPolicy Bypass -File .\docker\up-full.ps1`
   - 或（Linux/macOS）：`sh ./docker/up-full.sh`
3. 查看运行状态：
   - `docker compose -f docker/docker-compose.full.example.yml ps`
4. 停止并清理：
   - `docker compose -f docker/docker-compose.full.example.yml down`（默认**保留**命名卷，数据库与 `aura-api-storage` 不丢）
   - 或（Windows）：`powershell -ExecutionPolicy Bypass -File .\docker\down-full.ps1`
   - 或（Linux/macOS）：`sh ./docker/down-full.sh`
   - 需要连同卷一起删除（**会清空 PostgreSQL 等数据，慎用**）：`.\docker\down-full.ps1 -Volumes` 或 `sh ./docker/down-full.sh --volumes`
5. 健康检查：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\check-full.ps1`
   - Linux/macOS：`sh ./docker/check-full.sh`

## 镜像版本与仓库 SDK 对齐

- 后端构建使用的 `DOTNET_SDK_IMAGE` / `DOTNET_ASPNET_IMAGE` 默认与仓库根目录 `global.json` 中的 `sdk.version`（当前 `10.0.201`）一致。
- 升级 .NET SDK 时：先改 `global.json`，再同步修改 `docker/backend.Dockerfile` 的 ARG 默认值及 `docker/.env.full.example`、`docker/.env.prod.example`、`deploy-aura-ubuntu.sh` 中的同名变量；CI 中同步更新 GitHub Actions / Jenkins 里注入的 `DOTNET_SDK_IMAGE`、`DOTNET_ASPNET_IMAGE`（若使用 Secret 覆盖默认值）。

## 持久化策略（storage）

- **联调**（`docker-compose.full.example.yml`）：`api` 服务将命名卷 `aura-api-storage` 挂载到 `/app/storage`，避免容器重建丢失抓拍、导出与告警落盘等数据；前端仍为 `../frontend` 只读绑定挂载。
- **生产模板**（`docker-compose.prod.template.yml`）：同样挂载 `aura-api-storage` → `/app/storage`；静态资源若不由 API 容器提供，可不设置 `PATHS__FRONTENDROOT`，由网关或 CDN 托管前端；若由 API 托管，在编排中增加前端目录挂载并设置 `Paths__FrontendRoot`（见 `docker/.env.prod.example` 注释）。

## 企业网络适配

1. 在 `.env`（基于 `docker/.env.full.example`）中设置基础镜像变量：
  - `POSTGRES_IMAGE`、`REDIS_IMAGE`、`PYTHON_BASE_IMAGE`
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

- `docker-compose.prod.template.yml` 为占位模板，变量需由 Secret/配置中心注入；`API_IMAGE`、`AI_IMAGE` 对接私有仓库构建产物。
- 静态资源若不由 API 容器托管，可不设 `PATHS__FRONTENDROOT`；由 API 托管时需挂载前端目录并配置该变量（见 `docker/.env.prod.example`）。
- `api` 已挂载 `aura-api-storage` → `/app/storage`：告警路径、导出与抓拍落盘应与该卷或外部持久化一致，避免只写在容器可写层。
