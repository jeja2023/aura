# Docker 部署说明

Docker 目录已收敛为一套主入口：一份 Compose、一份 Docker 环境模板、一组启停/检查脚本。推荐流程是：部署服务器首次临时连接互联网完成镜像拉取/构建与启动，确认成功后断开互联网；后续升级通过离线镜像包上传更新。

## 文件结构

- `docker-compose.yml`：统一编排，启动 API、AI、PostgreSQL、Redis、ArangoDB，以及一次性 `arango-init` / `db-migrate`。
- `../.env.docker.example`：Docker 编排唯一环境模板，复制到仓库根目录为 `.env.docker` 后填写。
- `up.ps1` / `up.sh`：按 `.env.docker` 启动；默认不构建，首次联网部署可加 `-Build` / `--build`。
- `down.ps1` / `down.sh`：停止容器，默认保留命名卷。
- `check.ps1` / `check.sh`：检查 AI `/live`、AI `/ready`、API `/api/health`。
- `build-images.ps1` / `build-images.sh`：构建 `aura-api:local` 与 `aura-ai:local`。
- `save-images.*` / `load-images.*`：离线导出/导入业务镜像。
- `offline-pack.*`：生成完整离线部署/更新包，包含基础镜像、业务镜像和部署文件。
- `login-registry.*` / `push-images.*`：登录并推送业务镜像到内网仓库。
- `backend.Dockerfile` / `ai.Dockerfile`：业务镜像构建文件。

## 首次联网部署

1. 准备环境文件：
   - Windows：`Copy-Item .env.docker.example .env.docker`
   - Linux/macOS：`cp .env.docker.example .env.docker`
2. 编辑 `.env.docker`：
   - 首次联网部署时设置 `IMAGE_PULL_POLICY=missing`，允许 Docker 拉取缺失基础镜像。
   - 替换 `POSTGRES_PASSWORD`、`ARANGO_ROOT_PASSWORD`、`JWT__KEY`、`SECURITY__HMACSECRET`、`AURA_ADMIN_PASSWORD`。
   - 确认 `models/osnet_ibn_x1_0.onnx` 存在，或调整 `AURA_MODEL_HOST_DIR`。
3. 启动：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\up.ps1 -Build`
   - Linux/macOS：`sh ./docker/up.sh --build`
4. 检查：
   - Windows：`powershell -ExecutionPolicy Bypass -File .\docker\check.ps1`
   - Linux/macOS：`sh ./docker/check.sh`
5. 部署成功后，可断开互联网，并将 `.env.docker` 改回 `IMAGE_PULL_POLICY=never`，避免后续误拉远程镜像。

### 外部多 AI worker

- 如不使用 Compose 内置 `ai` 服务、而是让 API 直连局域网多个 AI worker，推荐在系统前端 `运行配置 -> AI 推理节点` 中维护地址；保存后写入 PostgreSQL，后端下一次 AI 请求立即生效，无需重启 API。
- `.env.docker` 中的 `AI_BASE_URLS` 只作为启动兜底，建议保持为空；当数据库运行时配置为空或不可用时，API 才回退到该值或 Compose 内置 `ai` 服务。
- 运行时配置支持英文分号、逗号或换行输入多个节点，后端会轮询这些节点，并在连接异常、`429`、`5xx` 时切换到下一个节点。
- 多 worker 仍需共用同一个 ArangoDB；如使用 `/ai/extract-file`，API 写入的图片路径必须在所有 AI worker 中同路径可读。

`down` 默认保留 PostgreSQL、Redis、ArangoDB 与 API storage 命名卷。确实要清空数据时才使用 `.\docker\down.ps1 -Volumes` 或 `sh ./docker/down.sh --volumes`。

## 构建镜像

当部署机需要从源码构建业务镜像时，先确保 `.env.docker` 中的基础镜像也能从本机或内网仓库获取，然后执行：

- Windows：`powershell -ExecutionPolicy Bypass -File .\docker\build-images.ps1`
- Linux/macOS：`sh ./docker/build-images.sh`

构建脚本会生成 `aura-api:local` 与 `aura-ai:local`，并按 `API_IMAGE_REPO`、`AI_IMAGE_REPO`、`IMAGE_TAG` 额外打标签；未指定 `IMAGE_TAG` 时自动使用时间戳。

## 镜像分发

推送到内网仓库：

1. 复制 `docker/.env.registry.example`，按实际环境设置 `REGISTRY_HOST`、`REGISTRY_USER`、`REGISTRY_PASSWORD`、`API_IMAGE_REPO`、`AI_IMAGE_REPO`、`IMAGE_TAG`。
2. 登录：`.\docker\login-registry.ps1` 或 `sh ./docker/login-registry.sh`
3. 推送：`.\docker\push-images.ps1` 或 `sh ./docker/push-images.sh`

离线导出/导入业务镜像：

- 导出：设置 `IMAGE_TAG` 后执行 `save-images.*`
- 导入：设置 `IMAGE_ARCHIVE_FILE` 后执行 `load-images.*`

基础镜像 PostgreSQL、Redis、ArangoDB、Python、.NET SDK/Runtime 需要由部署环境预先提供，或通过内网仓库地址写入 `.env.docker`。

## 断网后的升级更新

服务器断网后，后续升级在有互联网的构建机上准备离线包：

1. 准备 `.env.docker`，确保 `POSTGRES_IMAGE`、`REDIS_IMAGE`、`ARANGO_IMAGE`、`API_IMAGE`、`AI_IMAGE` 都是目标环境可使用的标签。
2. 构建业务镜像：`build-images.*`
3. 生成完整离线包：`offline-pack.*`
4. 将 `docker/dist/aura-offline-<时间戳>/` 整个目录上传到已断网服务器。

在已断网服务器上：

1. `docker load -i aura-images.tar`
2. 检查 `.env.docker`，保持 `IMAGE_PULL_POLICY=never`
3. 局域网启动：`docker compose --env-file .env.docker -f docker-compose.yml up -d --no-build`
更新时先 `docker load`，再用同一条 `docker compose ... up -d --no-build` 更新容器。默认保留命名卷，不会清空 PostgreSQL、Redis、ArangoDB 与 API storage 数据。
