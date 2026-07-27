# 寓瞳系统

本仓库已按《docs/archive/开发计划.md》《开发规范.md》完成第一至第五阶段开发，并完成通用媒体解析、多模数据架构和商业产品化升级，覆盖接入网关、可靠事件处理、统一事件/案件/调查、规则与 AI 治理、身份与数据治理、运行中心、商业工作台和发布门禁。

## 项目状态

- 当前版本：`0.3.0`（细目见 **`CHANGELOG.md`**）
- 阶段状态：商业产品化代码、数据库迁移、工作台、自动化测试和门禁框架已完成；真实依赖、目标硬件、客户 IdP 和真机适配器继续由发布门禁阻断
- 交付结论：实现与验收状态见 `docs/2026-07-23-0.3.0商业产品化实施与验收记录.md`；可销售口径以 `docs/commercial/能力与支持矩阵.md` 为准
- 工程状态：后端可构建（推荐打开根目录 **`Aura.sln`** 或 `dotnet build backend/Aura.Api/Aura.Api.csproj`）、前端页面可访问、核心链路可联调
- 运维状态：已提供回归脚本、联调压测脚本、部署与上线检查文档
- 修复记录：见根目录 **`2026-07-03-fix-optimization-notes.md`**，包含本轮数据库错误语义、前端覆盖层、GPU 网络预检和受限验证说明。
- 变更记录：见根目录 **`CHANGELOG.md`**。近期重点：
  - `0.1.24`：海康告警流相机缓存、ISAPI Handler 池化、列表端点 LIMIT 硬上限、`map_camera(device_id)` 索引、AI 检索失败原因维度与限流 FastAPI Depends 抽象。
  - `0.1.25`：Docker 部署入口收敛、临时联网部署/断网离线更新流程、运维脚本归档与上线手册合并。
  - `0.1.26`：局域网无 nginx 多 AI worker 直连，前端“运行配置”热更新 AI 节点，readiness 集群节点诊断，以及版本/镜像标签统一升级。
  - `0.1.27`：补充领导汇报版建设方案与 2026-06-09 自动化验收记录；后端、AI、前端与 Docker 配置级验收通过；记录本机 `docker-compose` / `docker compose` 命令兼容注意。
  - `0.1.28`：细粒度权限、产品化扩展管理页、查询索引与列表体验、AI 外部推理/离线评测、运维脚本和扩展页按钮视觉优化。
  - `0.1.29`：数据库故障语义收敛、前端覆盖层安全兜底、Docker GPU 网络预检和受限验证记录。
  - `0.1.30`：依赖漏洞审计清零、完整 .NET 构建/测试恢复、静态资源启动管线修复和临时产物清理。
  - `0.1.31`：构建源一致性、反向代理/Cookie/HMAC/CIDR 安全加固、Redis 降级、AI 图片输入限额、前端安全渲染与完整验证闭环。
  - `0.1.32`：本地一键启动自动执行数据库迁移，`.env`/`.env.docker` 与示例模板键集合和顺序对齐，并补齐自动/手动迁移边界说明。
  - `0.1.33`：CI 门禁扩展到 AI pytest 与前端覆盖脚本语法检查，补齐 AI 测试依赖、维护说明和路径保护测试的可移植性。
  - `0.2.0`：新增通用媒体解析控制面和标准提供方契约、可靠 Inbox/Outbox、pgvector 权威向量索引、ArangoDB 派生关系图、管理页面、模拟器及完整运维链路。
  - `0.2.1`：依赖治理与升级，前端 ESLint 10、AI 运行时依赖升级并迁移测试客户端到 httpx2，Dependabot 补齐 DbMigrator 生态与分组/忽略策略。
  - `0.3.0`：新增事件/案件/调查闭环、商业工作台、规则和 AI 治理、OIDC/应急身份、跨存储数据生命周期、通知、权益用量、移动 PWA、服务画像及证据化发布门禁。

## 目录结构

- `Aura.sln`：Visual Studio / Rider 解决方案入口
- `Directory.Build.props`：统一 MSBuild 中间输出路径（`.verify_build\obj`）并排除误编译 `obj` 生成物，便于本机工具链
- `Directory.Build.targets`：保持构建输入单一来源于 `backend/Aura.Api`，不再把 `generated/` 审查产物重定向进生产编译，确保本机、CI 与 Docker 编译同一套源码
- `backend/Aura.Api`：.NET 10 WebAPI 中枢服务；启动入口为 **`Program.cs`**，服务注册在 **`Extensions/ServiceExtensions.cs`**，路由按域拆分在 **`Extensions/AuraEndpoints*.cs`**，安全头与前端路由中间件在 **`Middleware/`**
- `backend/Aura.Api.Tests`：轻量自检工程（聚类/导出等），可选执行
- `backend/Aura.Api.Integration.Tests`：xUnit 集成测试（`WebApplicationFactory`，环境为 `Testing`）。**维护提示**：若修改 `backend/Aura.Api/appsettings.Testing.json` 中的 **`Jwt:Key` / `Jwt:Issuer` / `Jwt:Audience`**，必须同步修改 **`backend/Aura.Api.Integration.Tests/TestingJwt.cs`** 内同名常量，否则 `dotnet test` 会失败。
- `backend/Aura.Api/MediaAnalysis`：通用媒体解析提供方、任务/订阅控制面、可靠事件入口、业务投影和制品归档。
- `backend/Aura.Api/Vector`：pgvector 权威索引、检索路由、历史迁移、补偿和影子评测。
- `backend/Aura.Api/Graph`：PostgreSQL Outbox 到 ArangoDB 的关系图投影、重建和多跳查询。
- `ai`：Python FastAPI AI 服务（特征提取/检索），主入口 `main.py` 已收敛为应用装配入口，核心拆分为 `app/`（启动装配/生命周期/中间件）、`routes/`（API 路由）、`vector_store/`（向量索引存取）、`services/`（Arango/推理/聚类能力）、`models/`（请求模型）
- `database/schema.pgsql.sql`：PostgreSQL 表结构
- `frontend`：Vanilla JS 前端页面（根目录含 **`package.json`**，维护者可执行 **`npm ci`** 与 **`npm run lint`** 做 ESLint 检查）。**NVR 设备**与**海康 ISAPI 联调**分别对应 `frontend/device/` 与 `frontend/device-diag/`（入口见下文「关键页面入口」）
- `frontend/media-analysis`：提供方、管线、媒体源、订阅、任务、Inbox、制品、向量、图和 readiness 管理页面。
- `frontend/workbench`：统一商业工作台，覆盖事件、案件、调查、接入、规则/AI、数据治理、运行与运营分析，并提供静态壳 PWA。
- `tools/Aura.MediaAnalysis.ProviderSimulator`：通用解析提供方模拟器，用于图片、视频、视频流及故障场景联调。
- `deploy/k8s`：Kubernetes 示例（Ingress 拒绝公网 **`/metrics`**、NetworkPolicy 入站基线）与说明文档
- `寓瞳开放式集宿区智能分析系统建设方案（领导汇报版）.md`：面向立项/汇报的建设方案、预算测算、实施计划与验收指标说明
- `docs/抓拍链路端到端测试清单.md`：抓拍链路测试清单
- `scripts/ops/aura-ops.ps1`：统一运维脚本入口（上线就绪、AI 巡检、抓拍回归、全系统联调、商业发布门禁、数据库状态/备份/迁移/恢复）
- `docs/运维上线手册.md`：部署、上线检查、readiness 与 AI 生产检查统一手册
- `docs/通用媒体解析与多模数据架构开发计划.md`：PostgreSQL、pgvector、ArangoDB 的职责边界、完整开发方案和实施索引
- `docs/媒体解析平台运维手册.md`：提供方接入、事件重放、向量迁移、图重建和容量运维
- `docs/2026-07-22-0.2.0发布说明与验收记录.md`：本版本升级步骤、验证矩阵、已知边界和回滚原则
- `docs/2026-07-23-0.3.0商业产品化实施与验收记录.md`：商业产品化需求追踪、验证结果和仍需现场认证的硬门禁
- `docs/commercial`：服务画像、能力矩阵、发布门禁、身份恢复、数据删除、AI 治理、SLO 与 API 迁移文档
- `docs/2026-06-09-自动化验收记录.md`：`0.1.27` 本机自动化验收记录，覆盖后端构建/测试、AI pytest、前端 ESLint、Docker 配置解析与未覆盖现场项
- `docs/archive/最终交付清单.md`：历史交付范围清单

## 已落地核心能力

- 认证与权限：JWT + RBAC（超级管理员/楼栋管理员）
- 抓拍接入：海康 ISAPI 基线；ONVIF 为试验性契约，C++ SDK 网关为计划中能力，具体状态以能力矩阵和真机认证为准
- 抓拍链路：抓拍入库、AI 提特征、向量检索、重试队列
- 通用媒体解析：图片、视频和视频流任务编排，标准 HTTP 提供方、Webhook 和制品接入
- 可靠事件处理：HMAC 防重放、Inbox/Outbox、幂等消费、重试、死信和人工重放
- 多模数据存储：PostgreSQL 权威业务数据、pgvector 权威向量索引、ArangoDB 可重建关系图
- 空间引擎：楼层图、摄像头点位、ROI 编辑、空间碰撞、轨迹事件
- 业务研判：归寝、群租/异常滞留、夜不归宿
- 商业业务闭环：统一事件、案件、调查、证据、评论、状态机和 legacy 迁移
- 智能治理：规则影子/灰度/熔断/回滚，AI 评测阈值、反馈、漂移和发布审批
- 企业治理：OIDC + PKCE、MFA step-up、会话撤销、应急账号、留存/保全/删除和高风险操作保护
- 商业运营：通知升级/Web Push 网关、权益配额、用量成本、版本化业务 BI、现场移动处置 PWA 和证据化发布门禁
- 态势能力：SignalR 实时事件流，Three.js 3D 白模与 2D 切片下钻
- 统计与报表：ECharts 驾驶舱、CSV/XLSX 导出
- 外联输出：事件流与人员归属输出接口（含分页/筛选）

## 快速启动

### 1) 初始化数据库

空环境可将 `database/schema.pgsql.sql` 导入支持 pgvector 的 PostgreSQL 16+；推荐直接执行 `dotnet run --project backend/Aura.DbMigrator -- bootstrap`，它会登记合并基线 001-024 并执行增量 025-036。已有数据库只能执行增量 `migrate`，禁止使用 `bootstrap`。

### 2) 启动 AI 服务

```bash
cd ai
python -m venv .venv
# PowerShell 激活
.\.venv\Scripts\Activate.ps1
# 如提示执行策略限制，可先执行：
# Set-ExecutionPolicy -Scope Process Bypass
python -m pip install --upgrade pip
pip install -r requirements.txt
python -m uvicorn main:app --host 127.0.0.1 --port 8000
# 退出虚拟环境
deactivate
```

> 说明：若本机 `--reload` 模式不稳定，优先使用以上稳定启动命令。需要热重载时可改为 `python -m uvicorn main:app --host 127.0.0.1 --port 8000 --reload`。

> 维护者运行 AI 测试时，可先执行 `cd ai` 后的 `python -m pip install -r requirements-dev.txt`，再运行 `python -m pytest -p no:cacheprovider`。

### 3) 启动后端服务

```bash
cd backend/Aura.Api
dotnet run
```

> 直接运行 API 不会自动迁移数据库。已有数据库升级到当前版本前，请先执行 `dotnet run --project backend/Aura.DbMigrator -- migrate --command-timeout 300 --lock-timeout 60`。

说明：`AddAuraServices` 会根据 **`IHostEnvironment.ContentRootPath`** 自动解析仓库根下的 **`storage`** 目录（与 `Program.cs` 中静态文件挂载逻辑一致），用于抓拍归档、导出输出、资源上传等；向量接口图片/元数据长度上限可通过 **`Limits:MaxImageBase64Chars`**、**`Limits:MaxMetadataJsonChars`**（可选）覆盖默认值。

### 4) 打开前端

默认可直接通过后端同域名访问：`https://localhost:5001/`  
（后端已挂载项目根目录 `frontend` 为静态资源目录）

## 开发环境账号说明

- 开发环境启动时，后端会自动创建 `admin` 账号（若不存在）。
- 若已设置环境变量 `AURA_ADMIN_PASSWORD`，开发管理员将直接使用该密码。
- 若未设置 `AURA_ADMIN_PASSWORD`，系统会生成一组随机临时密码，仅在启动日志中输出，供首次登录使用。
- 若你需要再次触发开发环境的“重置开关”，可将 `appsettings.Development.json` 中 `Dev:ResetAdminPasswordOnce` 设为 `true`；随后请改回 `false`（仅为一次性重置）。
- 回归脚本与联调脚本不再内置默认密码，请先设置环境变量：`AURA_ADMIN_PASSWORD`。

> 生产环境请务必关闭开发自动建号能力，统一走正式账号流程，并替换 `appsettings.Production.json` 中全部占位密钥。

### 环境变量配置（跨平台）

- Windows PowerShell（当前会话）：
  - `$env:AURA_ADMIN_USER = "admin"`
  - `$env:AURA_ADMIN_PASSWORD = "你的密码"`
- Linux / macOS（当前会话）：
  - `export AURA_ADMIN_USER=admin`
  - `export AURA_ADMIN_PASSWORD='你的密码'`
- 模板文件：仓库已提供 **`.env.example`**，与根目录 **`.env` 结构完全一致**（同一批注释与键、同一顺序），仅将口令与密钥等替换为 **`REPLACE_*`** 占位符；复制为 `.env` 后填写真实值。`.env` 已在 `.gitignore` 中忽略，勿提交真实密码。维护仓库时若调整 `.env`，请同步更新 **`.env.example`**。Docker 编排专用变量仍以 **`docker/.env*.example`** 为准。
- 维护配置时要求 **`.env` vs `.env.example`**、**`.env.docker` vs `.env.docker.example`** 的键集合和顺序保持一致；新增配置项时先更新 example，再补齐实际环境文件。
- AI 新增可选键（见 `.env.example` 注释）：`Ai__BaseUrls`（启动兜底，运行后建议在前端“运行配置”页面维护）、`AURA_AI_INFER_BATCH_SIZE`、`AURA_AI_INFER_MAX_WAIT_SECONDS`、`AURA_AI_INFER_QUEUE_MAX_SIZE`、`AURA_AI_INFER_ENQUEUE_TIMEOUT_SECONDS`（推理批处理与背压）；`AURA_AI_HEALTH_VERBOSE`（健康接口是否输出详细内部错误，生产建议 `false`）；`AURA_AI_EXTRACT_FILE_ROOTS`（`;` 分隔允许目录，限制 `/ai/extract-file` 可访问路径）；`AURA_AI_MAX_IMAGE_BASE64_CHARS`、`AURA_AI_MAX_IMAGE_PIXELS`、`AURA_AI_ALLOWED_IMAGE_FORMATS`（AI 图片输入大小、像素数和格式白名单）；`AURA_AI_EVAL_DATASET_ROOTS`（限制 `/ai/evaluate-search` 可读取的离线评测集目录）。

### 本机一键启动与就绪检查

建议使用根目录一键脚本完成本机联调与就绪检查：

```powershell
cd e:\Aura
python start_services.py
```

- 脚本会在启动 AI 和 .NET 前自动执行 `Aura.DbMigrator migrate`，复用 `.env` / `appsettings.Development.json` 中的 PostgreSQL 连接串；如需临时跳过，可传 `--skip-db-migrate` 或设置 `AURA_SKIP_DB_MIGRATE=1`。

说明：
- **适用范围**：本机 **AI + .NET + PostgreSQL + Redis** 全栈联调；与仅跑 `dotnet test` 的 **`Testing` 环境**（可无 Redis/PG）不同。
- 脚本会优先读取根目录 `e:\Aura\.env`，在启动过程中先轮询 **AI `/live`** 进程存活，再轮询 **AI `/ready`**，要求 **HTTP 2xx** 且 JSON **`code=0`、`model_loaded=true` 且 `inference_ready=true`**；**.NET** 须 **`GET /api/health`** 为 **2xx** 且 **`code=0`** 且 `msg` 含「寓瞳」（避免误将 404 等响应当作就绪）。
- 就绪后会优先使用 **`AURA_ADMIN_PASSWORD`**（或 `.env`）登录并调用 **`GET /api/ops/readiness`**（超级管理员）。
- 若未提供 `AURA_ADMIN_PASSWORD`，脚本会尝试从启动日志里解析开发环境生成的临时密码作为兜底。
- 若两者都拿不到，脚本会保留基础健康检查成功结果，但跳过需要登录态的 readiness 深度检查，并给出提示。
- 若 `readiness` 输出中 `jwt=false / hmac=false`，请检查 `.env` 中 `Jwt__Key` 与 `Security__HmacSecret` 是否仍为占位值。
- 若用于 CI 预检，可使用 `python start_services.py --run-until-ready` 让脚本在就绪检查通过后直接退出。

### 集成测试（维护者）

- 命令：`dotnet test backend/Aura.Api.Integration.Tests/Aura.Api.Integration.Tests.csproj`
- 测试主机使用 `Testing` 环境，加载 `appsettings.Testing.json`，默认不连接本机 PostgreSQL 与 Redis。
- **请务必注意**：改动 `appsettings.Testing.json` 的 JWT 段时，同步更新 `TestingJwt.cs` 中的密钥与签发方/受众，避免集成测试与真实配置脱节。
- **运维探针**：存活检查建议使用 **`GET /api/health/live`**（无鉴权、无外部依赖）；业务向完整自检仍用 **`GET /api/ops/readiness`**（需超级管理员）。响应头 **`X-Correlation-Id`** 与请求同名校验或自动生成，便于排障。
- **生产主机头**：`appsettings.Production.json` 中 **`AllowedHosts`** 已改为占位域名，上线前请改为实际对外主机名（分号分隔多个）；开发环境可继续为 `*`。
- **反向代理与 Cookie**：生产经 Ingress/nginx/TLS 终止时保持 `Security__Cookies__ForceSecure=true` 与 `Security__ForwardedHeaders__Enabled=true`，并用 `Security__ForwardedHeaders__KnownNetworks__*` 收窄可信代理网段；本机纯 HTTP 联调可保持关闭。
- **Redis 降级**：API 进程复用单个 Redis 连接；Redis 不可用时缓存/重试队列降级，重负载接口限流会退回进程内固定窗口计数，避免直接放开。

### Docker 化建议

- 统一入口：`docker/docker-compose.yml`
- 环境模板：复制根目录 `.env.docker.example` 为 `.env.docker`，填写镜像标签、密码与密钥。
- 启动：`powershell -ExecutionPolicy Bypass -File .\docker\up.ps1` 或 `sh ./docker/up.sh`
- 检查：`powershell -ExecutionPolicy Bypass -File .\docker\check.ps1` 或 `sh ./docker/check.sh`
- 建议：生产环境优先使用 CI/CD Secret 或容器编排 Secret（如 Kubernetes Secret），避免明文进入镜像和仓库。

### 多 AI 节点（无 nginx）

- 生产环境推荐用前端 **运行配置** 页面维护多个 AI worker 地址：超级管理员登录后进入 `运行配置 -> AI 推理节点`，每行填写一个地址并保存，配置会写入 PostgreSQL 的 `sys_config`，下一次 AI 请求立即生效，无需重启 API。
- `.env` / `.env.docker` 中的 **`Ai__BaseUrls` / `AI_BASE_URLS`** 仅作为启动兜底，建议保持为空；当数据库运行时配置为空或不可用时，后端才回退到配置文件中的默认节点。
- 局域网双 GPU worker 示例：在前端填写 `http://127.0.0.1:9001` 与 `http://127.0.0.1:9002`。API 会按节点轮询请求，并在连接异常、`429`、`5xx` 时切换到下一个节点。
- `GET /api/ops/readiness` 与“运行配置”页面会返回 AI 节点总数、可达节点数、模型就绪节点数、推理可用节点数和节点列表，便于定位某个 worker 是否掉线、模型未加载或远程推理池不可用。
- 多 worker 部署必须共用同一个 ArangoDB；生产建议保持 `AURA_AI_REQUIRE_ARANGO=true`，避免节点退回各自内存索引后检索结果不一致。
- 如使用 `/ai/extract-file`，API 保存图片的路径必须在所有 AI worker 容器内同路径可读；否则请改用内联 Base64 或为 API 与 AI worker 统一挂载共享卷。

### 对接服务器 GPU worker

- 服务器 GPU 服务使用外部 Docker 网络 `gpu-bridge` 时，先确认网络已存在：`docker network ls | grep gpu-bridge`。主 `docker/docker-compose.yml` 已让 `ai` 容器加入 `gpu-bridge`，无需额外 override 文件。
- Windows 可先执行 `powershell -ExecutionPolicy Bypass -File .\docker-gpu-preflight.ps1` 检查，缺失时执行 `powershell -ExecutionPolicy Bypass -File .\docker-gpu-preflight.ps1 -Create` 创建；Linux/macOS 使用 `sh ./docker-gpu-preflight.sh --create`。
- `docker/docker-compose.yml` 已内置默认 GPU 配置：`http://gpu-worker-0:8000/predict;http://gpu-worker-1:8000/predict`、`project_name=person_reid`、`model_name=osnet_x1_0_v1.onnx`。现场不需要在 `.env.docker` 重复填写；只有服务名、项目名或模型名不一致时才覆盖同名环境变量。
- GPU 共享模型目录仍按 GPU 服务约定放置：`<shared-models>/<project_name>/<model_name>`，例如 `shared-models/person_reid/osnet_x1_0_v1.onnx`。本项目只传 `project_name`、`model_name` 和预处理后的 `tensor_data`，模型文件不再放在本项目 `models` 目录。
- `AURA_GPU_PREDICT_URLS` 支持英文分号、逗号或换行分隔多个地址，也支持 `URL|权重`，例如 `http://gpu-worker-0:8000/predict|2;http://gpu-worker-1:8000/predict|1`；单节点连续失败达到 `AURA_AI_BREAKER_FAIL_THRESHOLD` 后，会按 `AURA_AI_BREAKER_OPEN_SECONDS` 暂时跳过。
- 启用后，AI `/ready` 会返回 `inference_backend=gpu-worker`、`inference_ready=true` 与 `inference_remote` 节点池状态；未配置 GPU 时继续使用本地 ONNX，返回 `inference_backend=onnx`。

### 对接外部图片特征服务

- 若外部服务已经能自行完成图片解码、预处理和特征提取，可配置 **`AURA_EXTERNAL_EXTRACT_URLS`**，本项目 AI 服务会把 `/ai/extract` 的 `image_base64/metadata_json` 或 `/ai/extract-file` 的 `image_path/metadata_json` 原样转发给外部服务，并继续负责特征归一化、向量入库/检索、聚类、限流和审计。
- `AURA_EXTERNAL_EXTRACT_URLS` 支持英文分号、逗号或换行分隔多个地址，也支持 `URL|权重`；填写根地址时会自动补 `/ai/extract`。可选配置：`AURA_EXTERNAL_PROJECT_NAME`、`AURA_EXTERNAL_MODEL_NAME`、`AURA_EXTERNAL_API_TOKEN`、`AURA_EXTERNAL_TIMEOUT_SECONDS`。
- 外部图片特征服务优先级高于张量级 GPU worker：配置 `AURA_EXTERNAL_EXTRACT_URLS` 后，AI `/ready` 返回 `inference_backend=external-image`；仅配置 `AURA_GPU_PREDICT_URLS` 时返回 `gpu-worker`；两者都不配置时回退本地 ONNX。所有远程节点都熔断或不可达时，`/ready` 返回 `50302`。
- 外部服务响应中需能解析出特征向量，推荐返回 `{"code":0,"data":{"feature":[...]}}`；也兼容 `feature/features/embedding/embeddings/output/outputs/result/prediction/predictions` 等字段名。

## 关键页面入口

- 首页看板：`frontend/index/index.html`
- NVR 设备管理（含海康 ISAPI 诊断面板）：`frontend/device/device.html`
- 设备联调（独立海康 ISAPI 联调页）：`frontend/device-diag/device-diag.html`
- 三维态势：`frontend/scene/scene.html`
- 统计驾驶舱：`frontend/stats/stats.html`
- 报表导出：`frontend/export/export.html`
- 以图搜轨：`frontend/search/search.html`
- 运行配置：`frontend/ops-settings/ops-settings.html`
- 商业工作台：`frontend/workbench/workbench.html`（事件/案件协作、调查计划确认、移动待办、在线照片定位、深链与经营分析）

## 关键接口（示例）

- 存活探针（负载均衡/K8s）：`GET /api/health/live`
- 业务健康（中文提示）：`GET /api/health`
- AI 存活探针：`GET /live`（仅证明 AI 进程存活，适合容器 healthcheck）
- AI 就绪检查：`GET /ready` 或 `GET /`（返回 `code/msg`、`model_loaded`、`inference_ready` 与 `inference_backend`；后者可能为 `onnx`、`gpu-worker` 或 `external-image`。远程推理模式下同时返回 `inference_remote` 节点池状态；所有远程推理节点熔断或不可用时返回 `50302`。同时返回 `熔断状态`、`限流状态`、`回填状态` 三个可视化字段，并保留 `retrieval_guard`、`backfill_state`、`inference_queue` 结构化对象；生产环境默认脱敏 `arango_error/model_error`，可由 `AURA_AI_HEALTH_VERBOSE` 控制）
- AI 检索审计日志：`GET /ai/search-audit-logs?limit=100`（结构化 JSON，`data.items` 每条包含 `time/request_id/success/status/reason/hit_count/latency_ms/engine/strategy/filters_applied/warnings`）
- Prometheus 抓取（可选）：`GET /metrics`，由配置 **`Ops:Metrics:ExposePrometheus`** 控制（默认 `true`；集成测试所用 **`Testing`** 环境为 `false`）。生产环境建议仅允许监控网络或反向代理访问该路径；按路径在公网 Ingress 上拒绝的示例见 **`deploy/k8s/ingress-nginx-deny-public-metrics.example.yaml`**。
- OpenTelemetry 链路追踪（可选）：配置 **`Ops:Telemetry:EnableTracing`** 为 **`true`** 且设置 **`Ops:Telemetry:OtlpEndpoint`**（或环境变量 **`OTEL_EXPORTER_OTLP_ENDPOINT`**）；默认关闭。协议 **`Ops:Telemetry:OtlpProtocol`** 支持 **`Grpc`**（默认）与 **`HttpProtobuf`**。
- AI 推理节点运行时配置：`GET /api/ops/ai-settings`、`PUT /api/ops/ai-settings`（需 `ai.settings` 权限；超级管理员默认放行），用于前端热更新多 worker 地址。
- AI 服务访问控制（可选）：AI 进程读取 **`AURA_API_KEY`** 时，除根路径健康检查与 OpenAPI 文档外须在请求头携带 **`X-Aura-Ai-Key`**；.NET 侧配置 **`Ai:ApiKey`** 后由命名 **`HttpClient` 自动附加同名请求头。
- AI 重负载路由限流：`/ai/extract`、`/ai/extract-file`、`/ai/upsert`、`/ai/search`、`/ai/cluster` 统一接入检索保护限流；超过阈值时返回 `HTTP 429` 与 `code=42901`。
- AI 离线检索评测：`POST /ai/evaluate-search`（接收 `dataset` 或 `dataset_path`，返回 `recall_at_k`、`precision_at_k`、`mrr`、`hit_rate_at_k`、`empty_rate`、`failure_rate` 等质量指标；`dataset_path` 可用 `AURA_AI_EVAL_DATASET_ROOTS` 限制可读目录）。
- AI 文件提取路径约束：`/ai/extract-file` 在配置 `AURA_AI_EXTRACT_FILE_ROOTS` 后仅允许访问白名单目录内文件；越界返回 `HTTP 403` 与 `code=40301`。
- AI 图片输入硬化：`/ai/extract` 与本地 `/ai/extract-file` 会校验 Base64、图片格式、像素数和 Pillow 解码；非法图片返回 `HTTP 400/code=40002`，超过大小或像素限制返回 `HTTP 413/code=41301`。
- 登录：`POST /api/auth/login`
- 媒体规划（不代理音视频，仅能力/路径模板）：`GET /api/media/capabilities`、`POST /api/media/hikvision/stream-hint`（需 `device.diag` 权限）
- 海康告警长连接状态（联调用）：`GET /api/device/hikvision/alert-stream-status`（需 `device.diag` 权限）
- 抓拍接入：`POST /api/capture/push|sdk|onvif`
- 空间碰撞：`POST /api/space/collision/check`
- 研判执行：`POST /api/judge/run/daily`
- 统计概览（含 AI 运维指标）：`GET /api/stats/overview`
- 统计驾驶舱（含 AI 状态分布与链路趋势）：`GET /api/stats/dashboard`
- 导出：`GET /api/export/{type}?dataset=capture|alert|judge`（需 `export` 权限）
- 外联输出：`GET /api/output/events`、`GET /api/output/persons`

## 本机可观测性最小示例（可选）

以下为「能跑起来」的最小步骤；**不配置也不影响**日常开发。开发环境默认仅监听 **`https://localhost:5001`**（见 `backend/Aura.Api/Properties/launchSettings.json`）。

### Prometheus 抓取 `/metrics`

1. 确认 **`Ops:Metrics:ExposePrometheus`** 为 **`true`**（默认即可），或在本机 `.env` 中设置 **`Ops__Metrics__ExposePrometheus=true`**。
2. 准备 `prometheus.yml`（在容器内访问宿主机 API 时，Windows/macOS Docker 常用 **`host.docker.internal`**）：

```yaml
scrape_configs:
  - job_name: aura-api
    scheme: https
    tls_config:
      insecure_skip_verify: true
    metrics_path: /metrics
    static_configs:
      - targets: ["host.docker.internal:5001"]
```

3. 启动 Prometheus（示例）：

```bash
docker run --rm -p 9090:9090 -v /path/to/prometheus.yml:/etc/prometheus/prometheus.yml prom/prometheus
```

浏览器打开 `http://localhost:9090`，查询如 `http_request_duration_seconds`（具体指标名以 **prometheus-net** 导出为准）。

### OpenTelemetry 链路追踪（OTLP → Jaeger）

1. 启动 Jaeger（内置 OTLP，gRPC **4317**）：

```bash
docker run -d --name jaeger -p 16686:16686 -p 4317:4317 -p 4318:4318 jaegertracing/all-in-one:latest
```

2. 在本机 **`.env`** 中追加（或写入 `appsettings.Development.json` 的 **`Ops:Telemetry`** 段）：

```env
Ops__Telemetry__EnableTracing=true
Ops__Telemetry__OtlpEndpoint=http://127.0.0.1:4317
Ops__Telemetry__OtlpProtocol=Grpc
Ops__Telemetry__ServiceName=Aura.Api
```

等价可用标准变量：**`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317`**、**`OTEL_SERVICE_NAME=Aura.Api`**（与上一段可同时存在，以采集端文档为准）。

3. 重启 API 后，访问 **`http://localhost:16686`**，在 Jaeger UI 中选择服务 **`Aura.Api`** 查看 trace。

**说明**：若在 **`.env`** 中增加上述键，请同步把占位写法补进 **`.env.example`**（勿提交真实采集端内网地址若涉密）。

## 回归与压测

- 抓拍链路回归：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 capture-regression`
- 全系统联调压测：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 full-check`
- AI 检索巡检：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 ai-check`（检查 `熔断状态/限流状态/回填状态` 与检索审计日志；可通过后续参数透传 `-MaxLatencyMs`、`-MinRemainingQuota` 调整阈值）
- AI 检索巡检（CI JSON 模式）：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 ai-check -JsonOutput`（仅输出结构化 JSON，便于流水线解析；退出码仍为 `0/2/3`）
- AI 离线检索评测：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 ai-eval -SummaryOnly -MinRecall 0.9 -MinMrr 0.9 -MaxEmptyRate 0.05`（默认使用 `ai/retrieval_eval_sample.json`，现场标注集可用 `-DatasetPath` 替换）
- 数据库迁移状态：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-status`
- 数据库备份/迁移：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-backup`、`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-migrate`
- 数据库回滚：`powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-rollback -BackupFile .\artifacts\db-backups\<backup>.dump -ConfirmRestore -Clean -IfExists`；如需恢复后前滚到当前版本，使用 `db-rollback-migrate`

## 部署建议

- 参考 `backend/Aura.Api/appsettings.Production.json` 填充生产配置，并务必设置 **`AllowedHosts`** 为实际域名
- 参考 `docs/运维上线手册.md` 执行上线流程
- Docker 化参考：`docker/README.md`（已收敛为一套 Compose、一份环境模板和一组启停/检查脚本）
- Kubernetes：`deploy/k8s/README.md` 说明 NetworkPolicy 与按路径限制 **`/metrics`** 的关系，并提供 ingress-nginx 与 NetworkPolicy 示例清单
