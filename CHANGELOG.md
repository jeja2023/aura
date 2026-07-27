# 更新日志

本文档记录仓库关键版本与阶段性改动，便于联调、回归与发布追踪。

## 0.3.1（2026-07-27）

### 环境配置与 Docker 数据库集成

- 对齐根目录 `.env` 与 `.env.example` 结构（补齐缺少的 Web Push 提示注释）。
- 补全并严格对齐 `.env.docker` 与 `.env.docker.example` 的键集合、注释与结构（补充 `MEDIA_PROVIDER_SIMULATOR` 镜像/端口/监听地址、ArangoDB 派生图配置、通用媒体解析出站安全/超时参数等），同时保留本地自定义凭据。
- 自动同步宿主机 `.env` 的数据库与缓存连接凭据到 Docker 实例（PostgreSQL 密码、ArangoDB 密码以及 Redis 端口 `6380`），解决一键启动脚本 `start_services.py` 运行时的数据库认证失败问题。
- 新增本地冲突服务清理脚本 `stop_local_services.ps1`，支持在 Windows 上自动停用本地占用的 `5432` (PostgreSQL) 与 `8529` (ArangoDB) 端口。

### 前端 UI / 视觉规范与排版优化

- 修复外壳样式 `shell.css` 中高优先级选择器 `(0, 5, 1)` 导致的标签隐形问题：在 `button:not(...)` 排除列表中追加 `:not(.media-tab):not(.extensions-tab)`，消除全局白色文字规则导致 Tab 按钮在浅色背景下不可见的问题。
- 重构并统一“媒体解析平台”页面（`media-analysis.css`）标签样式（`.media-tab`），全面升级为与“扩展管理”（`extensions.css`）一致的现代化圆角胶囊/卡片按钮风格。
- 修复“媒体解析平台”与“扩展管理”页面长表单与大块 JSON 调试窗口溢出截断问题：在 `media-analysis.css` 与 `extensions.css` 中对 `.app-content` 配置 `overflow-y: auto`，使超出可视高度的内容能够自适应出现纵向滚动条。

### 版本号与构建配置

- 启用版本 `0.3.1`：`.NET` 统一版本写入 `Directory.Build.props` (0.3.1 / 0.3.1.0)，AI FastAPI OpenAPI 版本同步升级为 `0.3.1`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.3.1`。
- 更新 `README.md` 项目状态与近期重点清单。
- 新增 `docs/2026-07-27-0.3.1商业产品化实施与验收记录.md`，并同步当前能力与支持矩阵版本。

## 0.3.0（2026-07-26）

### 商业业务闭环

- 新增统一事件、案件、调查、证据和活动领域，提供版本冲突保护、事件去重、案件状态机、评论、事件关联、查询快照和 legacy 数据迁移对账。
- 新增统一商业工作台，覆盖事件/案件、调查、接入向导、规则与 AI、数据治理、运行中心和运营分析；提供响应式窄屏布局与只缓存静态壳的 PWA，并补齐我的待办、案件协作/清单、在线拍照定位、扫码深链与 Web Push 订阅。

### 治理与安全

- 新增规则影子/灰度执行、噪声抑制、每小时熔断、执行解释和版本回滚；新增 AI 评测持久化、阈值审批、人工反馈、漂移检测与治理仪表板。
- 新增 OIDC Authorization Code + PKCE、claim 映射审批、MFA step-up、服务端会话撤销和一次性应急账号；新增数据留存、法律保全、归档/删除作业和证据导出。
- 新增高风险操作预览、确认短语、幂等键、step-up 与审计保护，以及多通道通知/Web Push 网关、权益/配额、用量成本、版本化 BI 和移动草稿冲突同步能力。
- 受控自然语言调查支持用户修改白名单结构化计划后显式确认执行；越权字段、任意 URL、秘密、原始媒体和跨租户意图均由服务端策略拒绝。

### 交付与验证

- 新增数据库迁移 025-036；修正空库 bootstrap，使合并基线登记后在同一事务继续执行全部新迁移；036 补齐案件协作、跨存储删除投递、移动推送和版本化指标定义。
- 新增版本化服务画像、14 项商业发布门禁和 JSON/Markdown 证据包；缺少真实依赖、目标硬件、秘密扫描、客户 IdP 或真机适配器证据时明确阻断。
- 新增能力/适配器支持矩阵、身份恢复、数据删除、API 迁移、AI 治理和 SLO 手册。自动化验证结果见 `docs/2026-07-23-0.3.0商业产品化实施与验收记录.md`。

## 0.2.1（2026-07-26）

### 依赖治理与 Dependabot 策略

- `.github/dependabot.yml` 补充 `/backend/Aura.DbMigrator` NuGet 生态：该项目与 `Aura.Api` 共用 `Npgsql`，此前缺少配置会导致两者版本分叉。
- 七个生态全部启用 `groups`，补丁与次版本更新合并为单个 PR，避免依赖分支堆积。
- npm 侧单独设置 `eslint` 分组，强制 `eslint`、`@eslint/*` 与 `globals` 同步升级，避免扁平配置因 `js.configs.recommended` 版本错配而失败。
- 新增三条主版本忽略规则并在配置内写明原因：`Microsoft.OpenApi` 保持 2.x（该显式引用是 GHSA-v5pm-xwqc-g5wc 的安全覆盖，且 `Microsoft.AspNetCore.OpenApi` 10.0.x 依赖 2.x）、`StackExchange.Redis` 保持 2.x（SignalR Redis backplane 针对 2.x 编译）、`Npgsql` 保持 8.x（沿用既有决策，且须与 `Aura.DbMigrator` 同步升级）。

### 前端 ESLint 10 升级

- `eslint` 升级到 10.7.0，`@eslint/js` 同步升级到 10.0.1，`globals` 升级到 17.7.0；三者版本号不同步，需按各自最新稳定版配对。
- ESLint 10 的 `js.configs.recommended` 新增 `no-useless-assignment` 规则，修复 `extensions`、`log`、`ops-settings`、`role` 四个页面脚本中 `let data = null` 初值从未被读取的无用赋值；`frontend-overrides/extensions/extensions.js` 同步修改，保持覆盖层与主文件一致。
- `npm run lint` 通过，覆盖层脚本 `node --check` 语法检查通过。

### AI 依赖升级

- `fastapi` 0.115.6→0.136.1、`pydantic` 2.10.3→2.13.3、`numpy` 2.1.3→2.4.4、`onnxruntime` 1.20.1→1.25.0、`python-arango` 8.1.4→8.3.2。
- `fastapi` 0.136.1 带动 `starlette` 升级到 1.3.1，后者已弃用配合 `httpx` 使用 `TestClient`；测试依赖由 `httpx==0.28.1` 迁移到 `httpx2==2.9.1`，弃用告警消除。
- `python -m pip check` 无冲突；`python -m pytest -p no:cacheprovider` 32 项全部通过。

### 后端依赖对齐

- `Aura.Api.Integration.Tests` 的 `Microsoft.AspNetCore.Mvc.Testing` 由 10.0.5 对齐到 10.0.9，与 `Aura.Api` 中同为 10.0.9 的 ASP.NET Core 系列包保持一致。
- 本次未升级 `Microsoft.NET.Test.Sdk`：两个测试项目当前同为 17.14.1 且已对齐，升级到 18.x 涉及测试平台级变更，需单独排期并在 CI 中验证。

### GitHub Actions 升级

- `actions/cache` 4→5、`dorny/paths-filter` 3→4、`github/codeql-action` 3→4（`codeql.yml` 三处与 `trivy.yml` 的 `upload-sarif` 共四处同步）、`gitleaks/gitleaks-action` 2→3。
- `aquasecurity/trivy-action` 由 v0.35.0 对应的 SHA 升级到 v0.36.0 对应的 `ed142fd0673e97e23eac54620cfb913e5ce36c25`，保持不可变 SHA 固定策略，并同步修正其上方注释中的版本标注。
- 合并前经三方合并演算验证：0.1.33 引入的 AI Pytest 任务、前端覆盖层语法检查步骤与 `frontend-overrides` 路径过滤均完整保留，净变化仅为版本引用行。

### 版本

- 启用版本 `0.2.1`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.2.1`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.2.1`。

### 依赖分支清理

- 以下 Dependabot 分支的目标版本与本次升级结果一致，内容已实现，予以关闭：`fastapi-0.136.1`、`numpy-2.4.4`、`onnxruntime-1.25.0`、`pydantic-2.13.3`、`python-arango-8.3.2`、`Microsoft.AspNetCore.Mvc.Testing-10.0.9`。
- `eslint-10.7.0` 与 `globals-17.7.0` 两个分支因基点陈旧，合并会将 `@eslint/js` 退回 9.39.4、`globals` 退回 16.0.0 或 `eslint` 退回 9.39.4，造成回退，予以关闭。
- `Npgsql-10.0.3`、`StackExchange.Redis-3.0.17`、`Microsoft.OpenApi-3.8.0`、`Microsoft.NET.Test.Sdk-18.7.0` 保留待单独排期；其中 `Npgsql-10.0.3` 基点落后五个提交，其分支上的 `Aura.Api.csproj` 缺少 `Microsoft.OpenApi` 2.7.5 安全固定项且多个包版本偏低，解决冲突时须以 main 为准。

### 验证说明

- 前端与 AI 改动已在本机（Node 24.13.1、Python 3.12）完成验证。
- 后端 `.csproj` 改动因本机未安装 .NET SDK 未做编译验证，由 CI 门禁确认。
- GitHub Actions 升级仅能由 GitHub 侧运行验证，由 CI 门禁确认。

## 0.2.0（2026-07-22）

### 通用媒体解析架构与契约

- 新增通用媒体解析平台能力，Aura 可按租户配置解析提供方、处理管线、媒体源、视频流订阅和分析任务，覆盖图片同步解析、视频异步解析以及持续视频流解析；实现不绑定任何特定厂商客户端。
- 新增标准 HTTP 提供方适配器与 `IMediaAnalysisProvider` 扩展边界，支持能力发现、任务提交和查询、流订阅、媒体结果与制品拉取，后续接入其他解析平台只需实现统一契约。
- 新增 OpenAPI、标准事件 JSON Schema、Webhook 签名规范和凭据配置说明，明确 Aura 下发任务、提供方回推标准化事件、Aura 可靠消费的双向协议。
- 新增通用提供方模拟器，支持图片、视频、视频流、事件重放、制品获取以及超时、失败、重复、乱序等故障注入，可用于本地和集成环境联调。
- 新增《通用媒体解析与多模数据架构开发计划》和《媒体解析平台运维手册》，记录总体架构、职责边界、开发阶段、容量策略和故障处置流程。

### 提供方接入与安全

- 出站认证支持无认证、HMAC、Bearer、OAuth2 Client Credentials 和 mTLS；提供方 API 凭据 `secret_ref` 与入站 Webhook 凭据 `webhook_secret_ref` 分离管理。
- 密钥引用只允许环境变量、配置或 Secret 引用语法，拒绝把明文密钥写入业务表；提供方管理和诊断接口不回显密钥内容。
- 提供方 HTTP 调用增加连接/尝试/总超时、重试、熔断、按提供方并发控制以及请求和响应体积上限。
- 外部地址访问增加 SSRF 防护、主机白名单、私网和明文 HTTP 显式开关；媒体制品下载限制重定向、类型和大小。
- 入站 Webhook 增加 HMAC 签名、时间戳窗口、nonce 防重放、常量时间比较以及单条和批量事件入口。

### 可靠事件处理与业务投影

- 引入 PostgreSQL Inbox，事件接收先持久化再确认；Worker 使用租约和 `SKIP LOCKED` 并发领取，支持重试、死信、人工重放和积压观测。
- 对事件 ID、序列号、重复和乱序进行幂等处理；同一数据库事务内写入标准化事件、业务事实与 Outbox，避免重复投递产生重复告警或图关系。
- 新增检测事实、轨迹映射、抓拍、向量元数据与来源、身份候选、ROI、行为、告警/研判和制品归档投影；提供方轨迹、Aura 轨迹、实体和向量标识保持独立且可追溯。
- 新增任务状态轮询、流订阅续租、Inbox 消费、制品归档、向量补偿和图投影等后台 Worker，并纳入心跳与 readiness。

### PostgreSQL、pgvector 与 ArangoDB 分工

- PostgreSQL 作为配置、任务、原始事件、业务事实、Inbox/Outbox 和审计记录的唯一权威数据源，所有跨存储投影均可从 PostgreSQL 对账或重建。
- 启用 PostgreSQL `pgvector`，建立 512 维权威向量表和 HNSW 索引，支持按租户、模型版本和业务对象过滤的相似度检索；默认读写引擎均切换为 `pgvector`，旧 Arango 向量读取回退默认关闭。
- 新增向量路由、旧向量回填、迁移检查点、双写失败补偿和影子评测能力，为历史数据迁移、召回率对比和受控回滚保留操作路径。
- ArangoDB 保留为关系图专用存储，通过 PostgreSQL Outbox 异步投影空间、设备、人员、轨迹、访问和共现关系；禁止在业务请求中直接同步双写。
- 新增确定性的租户安全图键、图集合与边定义、投影检查点、失败重试/死信/重放、全量重建，以及摄像头可达性/路径、人员访问/共现、房间人员等图查询。
- 图查询增加最大深度、结果数、运行时间和响应体积限制；ArangoDB 不可用时 PostgreSQL 业务写入继续，恢复后由 Outbox 追平。

### 控制面、权限与前端

- 新增提供方、处理管线、媒体源、流订阅、分析任务、Inbox、制品、向量迁移、图投影和 readiness 管理 API。
- 新增媒体解析管理页面，按权限展示提供方、管线、来源、订阅、任务、Inbox、制品、向量、图和就绪状态，并复用当前租户上下文。
- 新增统一租户范围校验：超级管理员可跨租户，其他角色必须具备明确的 `tenant_role_scope`；全局提供方仅允许超级管理员管理。
- 内置楼栋管理员默认获得本租户媒体查看、管理、操作和图查询权限；事件重放、向量迁移和图管理继续要求显式高权限或超级管理员。
- 更新导航、角色权限配置和全局 readiness，使新能力从管理界面可发现、可授权、可操作。

### 可观测性与运维

- 新增提供方调用量、时延、超时分类、Webhook 认证与接收结果、Inbox 传输/处理延迟、任务结果与耗时、积压与最老事件年龄等指标。
- 新增 pgvector 检索、图投影/查询/重建以及 Worker 心跳指标；readiness 可分别报告 PostgreSQL/pgvector、提供方、Inbox、Outbox、制品归档和 Arango 图状态。
- Docker Compose 的 PostgreSQL 镜像切换为 `pgvector/pgvector:pg16`，ArangoDB 使用独立图数据库和最小权限账号；通用模拟器加入 `dev-tools` profile。
- 构建、推送、离线导出脚本统一处理 API、AI 和通用提供方模拟器三类业务镜像；离线包包含 pgvector 基础镜像、ArangoDB 和全部业务镜像。

### 数据库迁移

- 新增 `015` 至 `024` 共 10 个增量迁移，覆盖媒体解析控制面、Inbox/Outbox、pgvector、空间拓扑约束、投影检查点、业务事实、向量补偿、制品归档、凭据分离和默认权限。
- 空数据库 `bootstrap` 已验证应用 `001` 至 `024`，迁移状态为 applied 24、pending 0、unknown/drift 0；pgvector 0.8.5、pg_trgm 1.6 和 HNSW 索引均已确认可用。

### 验证

- `dotnet build Aura.sln --no-restore /nodeReuse:false`：通过，0 warning / 0 error。
- `Aura.Api.Tests`：84 passed；`Aura.Api.Integration.Tests`：46 passed。
- `python -m pytest -p no:cacheprovider ai/tests`：32 passed，保留第三方 multipart pending deprecation 与测试主动触发的 Pillow DecompressionBombWarning。
- `npm run lint`：通过。
- `docker compose --env-file .env.docker.example -f docker/docker-compose.yml config --quiet`：通过。
- 新 PostgreSQL + pgvector 数据库迁移、API readiness、租户隔离、图片/视频/视频流模拟器、HMAC 正反例、制品归档、向量写入检索和乱序事件路径均已通过端到端验证。
- 100 次投递同一个事件时，接收结果为 1 次 accepted、99 次 duplicate，数据库仅产生 1 条 Inbox、1 条标准事件、1 条检测事实和 1 条图 Outbox。
- `git diff --check`：通过；仅有 Windows Git 的 LF/CRLF 策略提示。

### 已知验证边界

- 真实 ArangoDB 容器验收因本机 Docker 镜像代理无法拉取 ArangoDB 3.12 镜像而未执行；图初始化、键规则、深度限制和投影逻辑已有自动化测试，生产发布前仍需在可访问镜像仓库的集成环境补跑真实图投影与遍历。
- 前端截图级视觉验收因本机浏览器执行环境初始化失败而未完成；前端 ESLint 已通过，仍需在目标浏览器做人工页面回归。
- 当前 Windows 环境没有 `sh`，因此 Linux shell 脚本未做本机语法执行验证；对应 PowerShell 脚本解析和 Compose 配置验证已通过。

### 版本

- 启用版本 `0.2.0`：`.NET` 程序集、AI FastAPI OpenAPI、默认业务镜像标签和离线包名称统一升级。
- 默认发布标签为 `v0.2.0`，默认离线归档名为 `aura-images-v0.2.0.tar`。

## 0.1.33（2026-07-13）
### CI 门禁与测试覆盖升级
- `.github/workflows/dotnet-ci.yml` 扩展触发范围，`push` / `pull_request` 与 frontend 变更检测均纳入 `frontend-overrides/**`，避免覆盖层脚本改动绕过前端门禁。
- 新增独立 `AI Pytest` job：使用 Python 3.12、pip 缓存、`ai/requirements-dev.txt` 安装测试依赖，执行 `python -m pip check` 与 `python -m pytest -p no:cacheprovider`。
- CI summary 增加 `Detect Changes` 与 `AI` 结果行，并在变更检测、后端、AI、前端任一应运行区域失败或取消时显式失败。
- 前端 job 在 `npm run lint` 后增加 `frontend-overrides/**/*.js` 的 `node --check` 语法检查，补齐 ESLint 基准目录外覆盖脚本的验证空白。

### AI 测试与维护文档
- 新增 `ai/requirements-dev.txt`，集中声明 AI 测试依赖，便于本机与 CI 使用同一套 pytest/httpx/pytest-asyncio 版本。
- `README.md` 补充 AI 测试维护命令，明确维护者可在 `ai` 目录安装 dev requirements 后运行 pytest。
- `ai/tests/test_ai_routes_and_index.py` 的 `/ai/extract-file` 路径保护测试改用 `tmp_path`，不再依赖本地 `.codex-artifacts` 可写性，提高 Windows、CI 与沙箱环境下的可移植性。
- `docs/运维上线手册.md` 的发布检查清单同步到 `0.1.33`，补充发布前需通过后端、AI、前端与覆盖层脚本检查。

### 验证
- `npm run lint`：通过。
- `node --check frontend-overrides/**/*.js`：通过。
- `python -m pip check`：通过。
- `python -m pytest -p no:cacheprovider`：32 passed，保留第三方 multipart pending deprecation 与刻意触发的 Pillow DecompressionBombWarning。
- `dotnet build Aura.sln --no-restore -v:minimal /m:1`：通过，0 warning / 0 error。
- `dotnet test Aura.sln --no-build --no-restore -v:minimal`：`Aura.Api.Tests` 45 passed，`Aura.Api.Integration.Tests` 42 passed。
- `git diff --check`：通过；仅有 Windows Git 的 LF/CRLF 策略提示。

### 版本
- 启用版本 `0.1.33`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.33`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.33`。

## 0.1.32（2026-07-03）
### 启动迁移与配置对齐
- `start_services.py` 在本地一键启动前自动执行 `Aura.DbMigrator migrate`，复用 `.env` / `appsettings.Development.json` 的 PostgreSQL 连接串，并使用 `DB_MIGRATION_COMMAND_TIMEOUT_SECONDS`、`DB_MIGRATION_LOCK_TIMEOUT_SECONDS` 控制超时。
- 新增 `--skip-db-migrate` 与 `AURA_SKIP_DB_MIGRATE=1`，用于临时跳过本地自动迁移；明确直接 `dotnet run --project backend/Aura.Api` 不会自动改库，需要先执行迁移器。
- 将 `.env` 对齐 `.env.example`、`.env.docker` 对齐 `.env.docker.example` 的键集合与顺序，保留本地真实值，补齐安全反代、AI 图片限制、外部 AI 服务和 forwarded headers 等新增配置键。
- 本地开发库已通过 `Aura.DbMigrator migrate` 应用 `001` 至 `014`，并以 `status --fail-on-pending --fail-on-drift` 确认为 applied 14、pending 0、unknown 0。

### 文档与验证
- README、Docker 说明、运维上线手册、数据库迁移说明和运维脚本说明同步记录自动迁移边界、env 对齐维护规则和跳过迁移开关。
- 验证记录：`.env` / `.env.docker` 与各自 example 的键集合、键顺序和重复键检查通过；`start_services.py` 语法解析与轻量导入检查通过；`git diff --check` 通过（仅 Windows LF/CRLF 策略提示）。

### 版本
- 启用版本 `0.1.32`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.32`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.32`。

## 0.1.31（2026-07-03）
### 综合修复与优化闭环
- 将前期检查中生成目录与正式源码不一致的问题收敛到 `backend/Aura.Api` 正式源码，`Directory.Build.targets` 改为说明性 no-op，避免生产构建继续引用 `generated/` 审查产物。
- `docker/backend.Dockerfile` 同步改为复制 `Directory.Build.targets` 并直接构建 `backend/Aura.Api` / `backend/Aura.DbMigrator`，确保本机、CI 与 Docker 使用同一套源码入口。
- 补齐 README、Docker 模板、上线手册与环境变量示例，使反向代理、Cookie、Redis 降级、AI 图片限制和镜像标签具备可交接说明。

### 后端安全与运行韧性
- 新增 `UseAuraForwardedHeaders`，通过 `Security:ForwardedHeaders` 控制反向代理头、可信代理 IP 与 CIDR 网段，生产默认启用可信内网段配置。
- 登录 Cookie 的 `Secure` 策略改为由生产环境和 `Security:Cookies:ForceSecure` 决定，适配 TLS 终止在 Ingress/nginx 的部署方式。
- HMAC 签名校验改为十六进制解析后使用 `CryptographicOperations.FixedTimeEquals`，减少计时侧信道风险。
- 抓拍来源白名单支持精确 IP、IPv4-mapped IPv6 与 CIDR；Redis 不可用时，重负载限流退回进程内固定窗口，避免保护逻辑直接放开。
- 新增 `RedisConnectionProvider`，缓存、固定窗口限流与重试队列复用连接并在 Redis 异常时优雅降级。

### AI 输入防护与错误语义
- `ai/utils/vector_utils.py` 增加严格 Base64 解码、Data URL 兼容、图片格式白名单、像素上限与 Base64 长度上限，配置项为 `AURA_AI_MAX_IMAGE_BASE64_CHARS`、`AURA_AI_MAX_IMAGE_PIXELS`、`AURA_AI_ALLOWED_IMAGE_FORMATS`。
- `/ai/extract` 在进入推理前完成图片校验：非法图片返回 `HTTP 400/code=40002`，超限图片返回 `HTTP 413/code=41301`。
- `/ai/extract-file` 调整为先解析并校验白名单路径，再检查文件存在性；本地解码路径复用统一图片校验逻辑，外部图片特征服务保持原始路径转发兼容。
- 补充 AI hardening 测试，覆盖 Data URL、非法 Base64、长度限制、像素限制与接口错误映射。

### 前端安全渲染与页面恢复
- `frontend/role`、`frontend/log`、`frontend/ops-settings`、`frontend/extensions` 的 HTML 转义 fallback 改为安全实现，避免 `window.aura.escapeHtml` 缺失时直接输出未转义文本。
- 恢复并整理角色、日志、运行配置与扩展管理脚本为干净 UTF-8 文件，保留筛选、分页、导出、保存配置与 readiness 展示等既有行为。
- 前端 ESLint 全量通过，修复过程中发现的 fallback 命名与解析问题已同步回归。

### 文档、配置与发布
- `.env.example`、`.env.docker.example` 与 `docker/docker-compose.yml` 新增 Cookie、Forwarded Headers、AI 图片限制相关环境变量。
- `docs/运维上线手册.md` 增加安全加固与 AI 图片输入限制上线核对项，便于生产部署前逐项确认。
- `README.md` 更新当前版本、构建源说明、反向代理/Cookie、Redis 降级和 AI 图片限制说明。
- `docker/.env.registry.example` 默认镜像标签与离线包文件名升级为 `v0.1.31`。

### 验证
- `dotnet build backend\Aura.Api\Aura.Api.csproj --no-restore -v:minimal /m:1`：通过，0 warning / 0 error。
- `dotnet test backend\Aura.Api.Tests\Aura.Api.Tests.csproj --no-restore -v:minimal /m:1`：45 passed。
- `dotnet test backend\Aura.Api.Integration.Tests\Aura.Api.Integration.Tests.csproj --no-restore -v:minimal /m:1`：42 passed。
- `python -m pytest -p no:cacheprovider ai\tests`：32 passed，保留第三方 multipart pending deprecation 与刻意触发的 Pillow DecompressionBombWarning。
- `npm run lint`：通过。
- `dotnet list ... package --vulnerable --include-transitive`：`Aura.Api`、`Aura.Api.Tests`、`Aura.Api.Integration.Tests` 均未发现易受攻击包。
- `git diff --check`：通过；仅有 Windows Git 的 LF/CRLF 策略提示。

### 版本
- 启用版本 `0.1.31`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.31`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.31`。

## 0.1.30（2026-07-03）

### 依赖安全审计闭环
- 前端 ESLint 开发工具链升级到 `eslint` / `@eslint/js` 9.39.4，带动 `@eslint/plugin-kit` 与 `js-yaml` 等传递依赖进入安全版本；`npm audit --json` 返回 0 个漏洞。
- 后端显式覆盖 `Microsoft.AspNetCore.OpenApi` 10.0.9 带入的 `Microsoft.OpenApi` 2.0.0，将 `Microsoft.OpenApi` 固定到 2.7.5，修复 GHSA-v5pm-xwqc-g5wc 高危审计项。
- 完整解决方案 NuGet 漏洞审计通过：`dotnet list Aura.sln package --vulnerable --include-transitive` 显示所有 .NET 项目无易受攻击包。

### 构建、测试与受限项恢复
- 审批服务恢复后，完成 `dotnet restore Aura.sln /p:NuGetAudit=true /p:NuGetAuditMode=all`、`dotnet build Aura.sln --no-restore -maxcpucount:1` 与 `dotnet test Aura.sln --no-build` 验证。
- 后端单元测试与集成测试全部通过：`Aura.Api.Tests` 39/39，`Aura.Api.Integration.Tests` 42/42。
- 清理遗留临时产物 `_write_test_root.txt`、`generated/project-preprocess.xml`、`generated/.msbuild`，并更新修复记录中的环境受限说明。
- 当前完整编译仍保留 generated `AuraEndpointsDomain.cs` 中两个未使用变量警告，不影响构建与测试结果。

### 静态资源管线启动修复
- 修复 generated 静态资源管线中 `PhysicalFileProvider` 创建时机：只有 `frontendRoot` 存在时才创建前端文件 provider。
- 隔离 content root 的集成测试不再因缺少 `frontend` 目录启动失败，`Storage目录不存在时启动会自动创建目录` 场景恢复通过。

### 版本
- 启用版本 `0.1.30`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.30`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.30`。

## 0.1.29（2026-07-03）

### 数据库错误语义与分页查询收敛
- 分页仓储结果统一增加 `Succeeded` 状态，覆盖抓拍、告警、操作日志、系统日志与用户列表，区分“真实空数据”和“数据库查询失败”。
- PG 已配置时，抓拍列表、告警列表、操作日志、系统日志、用户列表、外联输出与导出接口遇到数据库查询失败时返回 `50311`，不再以 HTTP 200 + 空列表掩盖故障。
- 统计概览与图表在数据库查询失败时抛出明确异常，由端点层映射为真正的 HTTP 500，避免把失败计数折算为 0。
- 导出抓拍与告警时改为分页累积读取，保留导出 `maxRows` 语义，同时复用列表查询上限和失败状态，降低单次大查询风险。

### 前端静态覆盖层与安全兜底
- 新增 `frontend-overrides` 静态资源覆盖目录，后端优先读取覆盖文件，再回退到原 `frontend` 目录，便于小范围修复前端问题而不扩大原目录改动面。
- 修复扩展管理页在 `window.aura.escapeHtml` 缺失时的 HTML 转义兜底，避免降级路径直接输出未转义文本。
- 前端路由中间件改为基于 `IFileProvider` 判断页面文件，使覆盖层中的同路径 HTML/JS 能参与静态路由解析。

### Docker GPU 网络预检与运维恢复说明
- 新增 `docker-gpu-preflight.ps1` 与 `docker-gpu-preflight.sh`，用于检查外部 Docker 网络 `gpu-bridge`；默认只检查，显式传 `-Create` / `--create` 时才创建网络。
- README 补充 GPU 网络预检命令，降低现场因 `gpu-bridge` 缺失导致 Compose 启动失败的排障成本。
- 新增 `2026-07-03-fix-optimization-notes.md`，记录本轮修复范围、审批/ACL 受限项、恢复命令和 AI `/ai/extract-file` 白名单配置建议。

### 依赖与构建验证
- 后端核心依赖继续升级到 `Microsoft.AspNetCore.* 10.0.9`、`Microsoft.Extensions.Http.Resilience 10.7.0`、`OpenTelemetry 1.16.0` 系列。
- 前端生产依赖审计通过：`npm audit --omit=dev --package-lock-only` 返回 0 个漏洞；剩余风险位于 ESLint 开发工具链，需在可写 `frontend` 目录后运行 `npm audit fix --package-lock-only`。
- 已用 `node --check frontend-overrides/extensions/extensions.js`、PowerShell 解析检查和 `dotnet msbuild -getItem:Compile` 验证覆盖层进入编译清单且原同名文件未重复编译。
- 当前受限环境仍无法完成完整 `dotnet build`：MSBuild 生成文件写入被沙箱阻止，提升权限请求被审批服务 502/503 拒绝；NuGet 漏洞审计也因 `api.nuget.org` TLS/凭据错误未完成。

### 版本
- 启用版本 `0.1.29`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.29`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.29`。

## 0.1.28（2026-06-13）
### 细粒度权限与高风险操作收敛

- 新增统一权限规范与别名归一化：`alert.manage`、`ai.settings`、`device.diag`、`export`、`space.manage`、`report.manage`、`tenant.manage`、`ai.platform`，超级管理员继续默认放行。
- 登录态接口 `/api/auth/me` 补充当前账号权限列表，前端可按权限动态显示功能入口。
- 角色管理页补充空间能力、报表计划、多租户、AI 平台等产品化扩展权限勾选项，并兼容历史权限别名。
- 导出、设备诊断、媒体能力、海康 ISAPI 调试、AI 运行配置、告警闭环与产品化扩展接口改为显式权限控制，降低高风险能力被普通楼栋角色误用的风险。
- 新增 `database/migrations/008_add_fine_grained_permissions.sql`，为既有 `building_admin` 保留告警处理、设备诊断和导出等常用能力位，避免升级后基础工作流断档。

### 产品化扩展管理

- 新增扩展管理页面 `frontend/extensions/`，统一承载告警闭环、空间能力、报表计划、多租户与 AI 平台配置等跨域产品化能力。
- 新增 `ExtensionRepository` 与 `/api/alert/workflow`、`/api/space/topology|heatmap`、`/api/report/*`、`/api/tenant/*`、`/api/ai-platform/*` 等接口，覆盖闭环处理、空间拓扑/热力快照、报表计划/生成记录、租户范围与 AI provider/A/B 实验配置。
- 新增 `database/migrations/014_add_workflow_space_report_tenant_ai_platform_tables.sql`，为上述扩展能力补齐 PostgreSQL 表结构与查询索引。
- 扩展管理页按当前账号权限过滤可见 tab；无授权时给出明确提示并隐藏新增/刷新等动作，避免展示不可操作的空功能。
- 优化扩展管理页 tab 按钮视觉：未选中按钮使用深色文字与浅蓝底，选中按钮使用主色实底与白字，修复按钮名称低对比度、看起来像空白按钮的问题。

### 查询性能与列表体验

- 新增告警时间、告警检索、抓拍筛选、日志时间、轨迹历史等索引迁移：`009_add_alert_time_lookup_index.sql` 至 `013_add_track_history_time_index.sql`。
- 抓拍、告警、日志、轨迹、统计与导出查询补充时间范围、关键字和分页场景下的索引友好查询路径，减少大数据量页面扫描和导出阻塞。
- 前端抓拍、告警、日志、搜索与统计页面同步优化筛选、分页、状态提示和空数据处理，提升排查与回归时的可读性。
- 导出能力扩展更多数据集和筛选参数，继续复用统一权限 `export` 控制。

### AI 推理、检索与评测

- AI 服务新增外部图片特征服务模式，配置 `AURA_EXTERNAL_EXTRACT_URLS` 后可将原始图片参数转发给外部服务，本项目继续负责特征归一化、检索、聚类、限流和审计。
- 张量级 GPU worker 地址支持 `URL|权重`，并与外部图片特征服务共用远程节点熔断逻辑；`/ready` 增加 `inference_ready`、`inference_backend` 与远程推理节点池状态。
- 新增 `/ai/evaluate-search` 离线检索评测接口、`ai/evaluate_search.py`、`scripts/ops/ai-eval.ps1` 与示例数据集，支持 recall、precision、MRR、命中率、空结果率和失败率等质量指标。
- AI 路由依赖、服务状态和推理服务进一步拆分，补充外部推理、限流、路径白名单和评测相关测试覆盖。

### 运维与工程结构

- `Program.cs` 与 `ServiceExtensions.cs` 继续拆分为应用装配、生命周期、授权、持久化、HTTP Client、限流、SignalR、海康服务等更细的注册模块，降低主入口维护成本。
- 运维脚本新增数据库状态、备份、迁移、恢复、回滚后前滚和 AI 离线评测入口；`scripts/ops/README.md` 与 `docs/运维上线手册.md` 同步补充使用说明。
- Docker 与环境模板补充外部图片特征服务、远程推理权重、熔断阈值和评测数据集目录等配置项。
- `database/schema.pgsql.sql` 同步最新迁移后的表结构，便于空库初始化与交付包基线保持一致。

### 版本与验证

- 启用版本 `0.1.28`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.28`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.28`。
- 本轮已确认前端 ESLint 通过：`npm run lint`。
- 后端目标测试已补充/更新 `AuraPermissionsTests`、`QueryOptimizationRegressionTests` 与 `AiClientTests` 相关覆盖；当前沙箱环境执行 `dotnet test backend\Aura.Api.Tests\Aura.Api.Tests.csproj --filter "FullyQualifiedName~AuraPermissionsTests|FullyQualifiedName~QueryOptimizationRegressionTests"` 时被 `bin/obj` 写入权限阻塞，需在本机非受限环境复跑。

## 0.1.27（2026-06-09）
### 文档与汇报材料补充

- 新增《寓瞳开放式集宿区智能分析系统建设方案（领导汇报版）》，面向立项/汇报场景补充建设背景、痛点、目标、架构、业务闭环、部署运维、预算测算、实施计划、验收指标与风险控制。
- 新增 `docs/2026-06-09-自动化验收记录.md`，沉淀本次本机自动化验收结果与边界，便于后续发布复核和现场联调交接。
- `README.md` 更新当前版本、近期重点、领导汇报版建设方案与验收记录入口，明确 `0.1.27` 为文档补强与自动化验收确认版本。
- `docs/运维上线手册.md` 与 `docker/README.md` 补充 `docker compose` / `docker-compose` 命令兼容说明，降低 Windows 现场 Compose 插件差异带来的部署摩擦。

### 自动化验收确认

- 后端 API 独立构建通过：`dotnet build backend\Aura.Api\Aura.Api.csproj`，0 警告 0 错误。
- 数据库迁移器独立构建通过：`dotnet build backend\Aura.DbMigrator\Aura.DbMigrator.csproj`，0 警告 0 错误。
- 后端单元测试通过：`dotnet test backend\Aura.Api.Tests\Aura.Api.Tests.csproj`，`16/16`。
- 后端集成测试通过：`dotnet test backend\Aura.Api.Integration.Tests\Aura.Api.Integration.Tests.csproj`，`42/42`。
- AI 测试通过：`python -m pytest`，`13/13`。
- 前端 ESLint 通过：`npm run lint`。
- Docker 配置级验收通过：`docker-compose --env-file .env.docker.example -f docker\docker-compose.yml config --quiet` 与 `docker-compose --env-file .env.docker -f docker\docker-compose.yml config --quiet` 均通过。
- 环境模板键集合校验通过：`.env` 与 `.env.example` 一致，`.env.docker` 与 `.env.docker.example` 一致。
- 运维/Docker PowerShell 脚本语法检查通过：`scripts/ops/*.ps1` 与 `docker/*.ps1` 关键脚本均无解析错误。

### 运行环境兼容记录

- 本机 `docker compose` 子命令不可用，但 `docker-compose` v2.40.3 可用；离线服务器如遇同类 Docker CLI 差异，可使用连字符版 `docker-compose` 执行配置解析与启动命令。
- 沙箱内 `.NET restore/build` 与 `pytest` 会受到 NuGet 网络、临时目录和缓存目录写入权限影响；本次验收使用非沙箱执行与 `.codex-build` 隔离目录复核，排除项目代码问题。
- 真实 PostgreSQL/Redis/ArangoDB、AI/API 全栈服务、真实海康/ONVIF/NVR/GPU worker 未在本轮启动联调，仍需现场按 `docs/运维上线手册.md` 执行 readiness、抓拍回归与全系统联调。

### 版本与分发

- 启用版本 `0.1.27`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.27`。
- `docker/.env.registry.example` 默认业务镜像标签与离线包文件名升级到 `v0.1.27`。

### 外部 GPU/AI 推理兼容

- AI 推理服务新增外部图片特征服务模式：配置 `AURA_EXTERNAL_EXTRACT_URLS` 后，`/ai/extract` 与 `/ai/extract-file` 会将原始图片参数转发给外部服务，由外部服务负责图片解码、预处理和特征提取；本项目继续负责特征归一化、向量索引、检索、聚类、限流和审计。
- 保留原有张量级 GPU worker 模式：仅配置 `AURA_GPU_PREDICT_URLS` 时，本项目 AI 服务继续负责图片解码和预处理，只将 `tensor_data` 发往外部 `/predict`。
- 外部推理优先级调整为：`external-image`（外部图片特征服务） -> `gpu-worker`（张量级 GPU worker） -> `onnx`（本地 ONNX）。`GET /ready` 的 `inference_backend` 可用于确认当前模式。
- `.env.example`、`.env.docker.example` 与 `docker/docker-compose.yml` 补充 `AURA_EXTERNAL_EXTRACT_URLS`、`AURA_EXTERNAL_PROJECT_NAME`、`AURA_EXTERNAL_MODEL_NAME`、`AURA_EXTERNAL_API_TOKEN`、`AURA_EXTERNAL_TIMEOUT_SECONDS` 配置。

## 0.1.26（2026-05-28）
### 抓拍与事件时间时区修复

- 修复抓拍记录、三维空间态势事件流在前端显示时多加 8 小时的问题：全局 `formatDateTimeDisplay` 对 `yyyy-MM-dd HH:mm:ss` / 无时区 ISO 字符串按后端展示时间原样格式化，不再交给浏览器 `Date` 二次本地化。
- 三维空间态势事件流排序改为显式按本地无时区时间解析，避免展示时间正确但排序时间又被浏览器按 UTC/本地规则差异化处理。
- `capture_record.capture_time`、`track_event.event_time`、`alert_record.created_at`、`virtual_person.first_seen/last_seen` 这类 PostgreSQL `TIMESTAMP` 字段写入与范围查询统一使用本地墙钟时间，避免将 UTC instant 写入无时区字段后造成后续展示漂移。
- `DateTimeOffset` JSON 展示序列化统一转换为 API 所在时区的本地时间后输出 `yyyy-MM-dd HH:mm:ss`，与 `DateTime` 展示语义保持一致。
- 开发环境近 7 日种子数据不再为“今天”生成当前时间之后的抓拍、告警与轨迹；启动时会自动修正已存在的 dev-seed 当日未来记录，避免中午启动后页面出现 18:00、19:00 等疑似时区加 8 小时的数据。


### 局域网多 AI worker 直连适配

- 后端 AI 客户端由单一 `Ai:BaseUrl` 升级为多节点运行时配置，新增 `sys_config` 表保存 `ai.base_urls`，超级管理员可在前端“运行配置”页面热更新多个 AI worker 地址，无需重启 API。
- `AiClient` 增加节点归一化、运行时配置读取、轮询调度与故障转移能力，请求会在当前生效节点间轮询分配，并在连接异常、超时、`429`、`500`、`502`、`503`、`504` 等可重试故障下自动尝试下一个节点。
- AI 调用失败信息补充 `endpoint=...`，便于在无 nginx 的局域网部署中快速定位具体异常节点。
- 保留旧的 `Ai__BaseUrl` / `Ai:BaseUrl` 单节点配置与 `Ai__BaseUrls` / `AI_BASE_URLS` 多节点配置作为启动兜底；运行时数据库配置为空或不可用时才回退启动配置。

### 就绪检查与生产配置加固

- `/api/ops/readiness` 从单 AI 健康检查升级为集群视角，新增 `ai.configuredNodes`、`ai.reachableNodes`、`ai.modelLoadedNodes` 与 `ai.nodes` 详情；只要至少一个节点可达即认为 `ai_service` 可用，至少一个节点模型加载成功即认为 `ai_model` 可用。
- 新增 `GetClusterHealthAsync`、`AiClusterHealth`、`AiEndpointHealth`，保留 `GetHealthAsync` 兼容旧调用，并优先返回健康节点的响应。
- 生产启动校验改为同时识别 `Ai:BaseUrls` 与 `Ai:BaseUrl`，对多节点 URL 执行 HTTP/HTTPS 绝对地址校验，避免发布后才暴露配置错误。
- 新增 `GET /api/ops/ai-settings` 与 `PUT /api/ops/ai-settings`，仅超级管理员可访问；保存时校验 URL、刷新运行时缓存并写入操作日志。
- 兼容未执行 `007_add_sys_config.sql` 的存量库：读取运行时配置遇到 `sys_config` 缺表时回退启动配置，保存运行时配置时仍明确提示先执行迁移。

### Docker 与环境模板

- `.env` 与 `.env.example` 同步保留 `Ai__BaseUrls` 启动兜底项，推荐保持为空并在前端“运行配置”页面维护现场 GPU worker。
- `.env.docker` 与 `.env.docker.example` 同步保留 `AI_BASE_URLS` 启动兜底项，`docker/docker-compose.yml` 仍为 API 注入 `Ai__BaseUrls` 以兼容数据库运行时配置不可用的场景。
- `.env.docker` 与 `.env.docker.example` 重新对齐键集合，补齐 AI 推理批处理、队列背压、检索限流、熔断、健康脱敏、检索默认值与数据库迁移超时参数，避免现场配置文件缺键导致 Compose 只能使用隐式默认值。
- `docker/docker-compose.yml` 为 AI 容器显式透传 `AURA_AI_INFER_*`、`AURA_AI_SEARCH_RATE_LIMIT_PER_MINUTE`、`AURA_AI_BREAKER_*`、`AURA_AI_HEALTH_VERBOSE`、`AURA_AI_EXTRACT_FILE_ROOTS`、`AURA_AI_INDEX_SNAPSHOT_PATH` 与检索策略默认值，便于企业现场按 CPU/GPU worker 能力和抓拍峰值压测结果调优。
- `db-migrate` 服务启动命令改为带 `--command-timeout` 与 `--lock-timeout` 参数执行，参数由 `.env.docker` / `.env.docker.example` 中的 `DB_MIGRATION_COMMAND_TIMEOUT_SECONDS` 与 `DB_MIGRATION_LOCK_TIMEOUT_SECONDS` 控制。
- `backend/Aura.Api/appsettings*.json` 增加 `Ai:BaseUrls` 占位项，使开发、测试、生产配置结构保持一致。
- 新增 `database/migrations/007_add_sys_config.sql`，基线 schema 同步增加 `sys_config` 运行时配置表。
- `docker/.env.registry.example` 的默认业务镜像标签与离线包文件名升级到 `v0.1.26`。

### 数据库升级迁移企业级加固

- `backend/Aura.DbMigrator` 新增 PostgreSQL advisory lock，`migrate` 与 `bootstrap` 执行前会先获取迁移锁，防止多实例 API、重复发布任务或多个 `db-migrate` 容器并发升级同一数据库。
- 新增 `--command-timeout <sec>`，通过 `statement_timeout` 约束单次 SQL 执行时长，避免发布窗口内长时间无界等待；默认值为 300 秒。
- 新增 `--lock-timeout <sec>`，迁移锁等待超时后以退出码 `3` 失败，便于流水线识别“已有迁移正在执行”与普通 SQL 错误。
- `status` 命令新增 `--fail-on-pending` 与 `--fail-on-drift`，可分别用于发布后确认无待执行脚本，以及发布前发现数据库存在当前交付包未知的迁移历史。
- 迁移脚本加载阶段新增重复版本检测，遇到两个相同版本号的 `*.sql` 会直接失败，避免 `007_xxx.sql` 与 `007_yyy.sql` 同时进入交付包。
- `status` 输出补充 unknown applied migrations 统计，用于识别目标库 schema 版本领先于当前制品、交付包不完整或人工改写迁移历史的风险。
- 已执行过的迁移继续通过 `schema_migrations.checksum` 做不可变校验；生产升级要求新增脚本向前追加，不改写已落地脚本。
- `database/migrations/README.txt` 补充企业发布用命令：`status --fail-on-drift`、`migrate --command-timeout 300 --lock-timeout 60`、`status --fail-on-pending --fail-on-drift`，并明确 `bootstrap` 只允许用于空库。

### 文档同步

- `README.md` 更新当前版本号与“多 AI 节点（无 nginx）”部署说明，补充前端热更新、轮询、故障转移、readiness 节点统计、共享 ArangoDB 与 `/ai/extract-file` 共享路径注意事项。
- `docker/README.md` 增加“外部多 AI worker”部署说明，明确生产优先通过前端运行配置维护地址，`AI_BASE_URLS` 仅作为启动兜底。
- `docs/运维上线手册.md` 同步 readiness 返回字段与生产检查清单，强调 `sys_config` 迁移、多 worker 运行时配置、共享 ArangoDB、共享卷路径一致性，以及路径不可共享时回退 Base64 的取舍。
- `docs/运维上线手册.md` 新增“数据库升级迁移”章节，明确 PostgreSQL 升级迁移不是服务器迁移，并补充备份、预检、执行、后验、失败回滚与漂移处理流程。

### 版本与验证

- 启用版本 `0.1.26`：`.NET` 统一版本写入 `Directory.Build.props`，AI FastAPI OpenAPI 版本同步为 `0.1.26`，README 与离线镜像模板同步更新。
- 新增 `AiClientTests` 覆盖多节点配置解析（含换行分隔输入）、非法 URL 拒绝、轮询分发、故障转移、运行时热更新与集群健康统计。
- 验证记录：`dotnet test backend\Aura.Api.Tests\Aura.Api.Tests.csproj --no-build` 基于现有构建产物通过；完整重编译需释放本机 `dotnet.exe` 对 `obj/bin` 产物的占用后复跑。
- 验证记录：`.env.docker` 与 `.env.docker.example` 键集合一致，无缺失或多余键；`docker-compose --env-file .env.docker -f docker\docker-compose.yml config --quiet` 通过。
- 验证记录：`git diff --check -- backend\Aura.DbMigrator\Program.cs docker\docker-compose.yml .env.docker.example database\migrations\README.txt docs\运维上线手册.md` 通过。
- 未完成项：`dotnet build backend\Aura.DbMigrator\Aura.DbMigrator.csproj` 在当前本机环境被 MSBuild/NuGet 临时文件写入权限阻塞，错误为 `Access to the path ... is denied`；已尝试隔离中间目录，仍需在释放/修复本机工作区权限后复跑完整编译。

## 0.1.25（2026-05-26）

### Docker 部署入口收敛

- `docker/` 目录由多套 `full/lan/prod/ops-check` 示例入口收敛为一套主入口：
  - 新增 `docker/docker-compose.yml`：统一启动 API、AI、PostgreSQL、Redis、ArangoDB，以及一次性 `arango-init` 与 `db-migrate`。
  - 新增根目录 `.env.docker.example`：作为唯一 Docker 编排环境模板，复制为 `.env.docker` 后使用。
  - 新增 `docker/up.ps1` / `docker/up.sh`、`docker/down.ps1` / `docker/down.sh`、`docker/check.ps1` / `docker/check.sh`：统一启停与健康检查入口。
  - 删除旧入口：`docker-compose.full.example.yml`、`docker-compose.prod.template.yml`、`docker-compose.ops-check.example.yml`、`up-full.*`、`down-full.*`、`check-full.*`、`deploy-aura-ubuntu.sh`、`Jenkinsfile.docker.example`、`.env.full.example`、`.env.prod.example`。
- `docker/build-images.*` 改为读取 `docker/docker-compose.yml` 与根目录 `.env.docker`，并按 `.env.docker` 中实际 `API_IMAGE` / `AI_IMAGE` 识别构建产物后再打 `API_IMAGE_REPO:IMAGE_TAG` / `AI_IMAGE_REPO:IMAGE_TAG` 标签。
- `docker/save-images.*`、`docker/load-images.*`、`docker/login-registry.*`、`docker/push-images.*` 输出与错误提示改为 ASCII，减少 Windows PowerShell 编码差异带来的解析或日志问题。
- `.gitignore` Docker env 白名单同步收敛：保留根目录 `.env.docker.example` 与 `docker/.env.registry.example`，移除旧 `.env.full/.env.prod` 示例白名单。
- 环境变量文件职责重新对齐：
  - `.env` 与 `.env.example` 保持同一套“本机直跑/开发调试”配置键。
  - `.env.docker` 与 `.env.docker.example` 保持同一套“Docker Compose 部署”配置键。
  - 两组配置允许数量不同，因为本机直跑使用双下划线应用配置键，Docker 部署额外包含镜像名、端口绑定、挂载路径与离线更新策略等编排参数。
- 修正 Docker 构建基础镜像默认值：
  - `DOTNET_ASPNET_IMAGE` 由不存在的 `mcr.microsoft.com/dotnet/aspnet:10.0.201` 改为官方运行时标签 `mcr.microsoft.com/dotnet/aspnet:10.0`。
  - `PYTHON_BASE_IMAGE` 保持为 `python:3.12-slim`，并与 `docker/ai.Dockerfile` 默认值一致。
- 生产 Docker 首次启动补齐初始管理员引导：当 `sys_user` 为空且配置了 `AURA_ADMIN_PASSWORD` 时，API 会使用 `AURA_ADMIN_USER` / `AURA_ADMIN_PASSWORD` 创建 `super_admin` 管理员；已有用户时不会重置密码。
- Docker 时区统一为东八区：
  - `.env.docker.example` / `.env.docker` 新增 `TZ=Asia/Shanghai` 与 `PG_TIMEZONE=Asia/Shanghai`。
  - Compose 为 PostgreSQL、Redis、ArangoDB、AI、API、迁移容器注入 `TZ`。
  - PostgreSQL 启动参数设置 `timezone` / `log_timezone`，API 与迁移连接串追加 `Timezone=Asia/Shanghai`。
  - API 控制台日志正文增加 `yyyy-MM-dd HH:mm:ss zzz` 时间戳，容器 stdout 中可直接看到东八区业务日志时间。
- 日志页标签误判修复：操作日志不再因为页面标题包含“异常研判”等业务词而标记为“失败”。

### 临时联网部署与断网后离线更新

- 明确推荐交付路径：
  1. 部署服务器首次临时连接互联网。
  2. `.env.docker` 设置 `IMAGE_PULL_POLICY=missing`。
  3. 执行 `docker/up.ps1 -Build` 或 `docker/up.sh --build`，完成基础镜像拉取、业务镜像构建与容器启动。
  4. 通过 `docker/check.*` 验证成功后，将 `.env.docker` 改回 `IMAGE_PULL_POLICY=never` 并断开互联网。
  5. 后续升级由有网构建机生成离线包，上传到断网服务器后 `docker load` + `docker compose ... up -d --no-build` 更新。
- `docker/up.ps1` / `docker/up.sh` 新增 `-Build` / `--build`：仅用于首次临时联网部署或本机构建；默认仍使用 `--no-build`，适合断网环境复启与更新。
- `docker/docker-compose.yml` 的端口绑定更安全：
  - API 默认绑定 `0.0.0.0:${API_PORT}`。
  - AI、PostgreSQL、Redis、ArangoDB 默认绑定 `127.0.0.1`，避免临时联网部署期间将内部服务直接暴露到外部网络。
  - 新增 `API_BIND_ADDRESS`、`AI_BIND_ADDRESS`、`POSTGRES_BIND_ADDRESS`、`REDIS_BIND_ADDRESS`、`ARANGO_BIND_ADDRESS` 环境变量，可按现场需要覆盖。
- 新增 `docker/offline-pack.ps1` / `docker/offline-pack.sh`：生成完整离线更新包，包含：
  - 基础镜像：PostgreSQL、Redis、ArangoDB。
  - 业务镜像：Aura API、Aura AI。
  - 部署文件：`docker-compose.yml`、`.env.docker`、`database/`、`frontend/`、`models/`。
  - 离线包内 README，说明断网服务器上的 `docker load` 与 `docker compose up -d --no-build` 更新步骤。
- `docker/.env.registry.example` 补充说明：`offline-pack.*` 生成的是完整离线部署/更新目录，不只是业务镜像 tar。

### 运维脚本收拢

- 根目录运维脚本移入 `scripts/ops/`，根目录不再堆放回归/巡检脚本：
  - `AI检索巡检脚本.ps1` → `scripts/ops/ai-check.ps1`
  - `上线就绪检查脚本.ps1` → `scripts/ops/readiness-check.ps1`
  - `抓拍链路回归脚本.ps1` → `scripts/ops/capture-regression.ps1`
  - `全系统联调与压测脚本.ps1` → `scripts/ops/full-check.ps1`
- 新增 `scripts/ops/aura-ops.ps1` 作为统一入口：
  - `readiness`
  - `ai-check`
  - `capture-regression`
  - `full-check`
- `aura-ops.ps1` 读取子脚本时使用 `Get-Content -Encoding UTF8` + `ScriptBlock` 执行，避免 Windows PowerShell 5 直接执行 UTF-8 无 BOM 中文脚本时出现乱码解析问题。
- 新增 `scripts/ops/README.md`，说明统一入口与子脚本用途。

### 文档合并与归档

- 新增 `docs/运维上线手册.md`：合并部署手册、上线检查、readiness 使用说明与 AI 生产检查，作为当前上线与日常巡检统一入口。
- 将历史/过期文档移入 `docs/archive/`：
  - `docs/部署文档与运维手册.md`
  - `docs/上线检查清单.md`
  - `docs/readiness运维使用说明.md`
  - `docs/AI生产完整性检查清单.md`
  - `docs/最终交付清单.md`
  - `开发计划.md`
- 新增 `docs/archive/README.md`，明确归档文档仅用于追溯。
- `README.md` 同步更新：
  - Docker 化说明改为统一 `docker/docker-compose.yml` + `.env.docker` + `up/check/down`。
  - 回归与巡检命令改为 `scripts/ops/aura-ops.ps1`。
  - 部署建议改为参考 `docs/运维上线手册.md`。
  - 修正部分文档路径，避免根目录/`docs/` 位置混淆。
- `docker/README.md` 重写为当前单入口部署说明，补充“首次临时联网部署”和“断网后的升级更新”两条流程。

### 验证记录

- `docker-compose --env-file .env.docker.example -f docker/docker-compose.yml config --quiet` 通过。
- `.env` / `.env.example` 键集合一致，`.env.docker` / `.env.docker.example` 键集合一致。
- `docker-compose --env-file .env.docker -f docker/docker-compose.yml config --quiet` 通过。
- Docker PowerShell 脚本解析通过：`up.ps1`、`down.ps1`、`check.ps1`、`build-images.ps1`、`save-images.ps1`、`load-images.ps1`、`login-registry.ps1`、`push-images.ps1`、`offline-pack.ps1`。
- `scripts/ops/aura-ops.ps1` 解析通过。
- 已确认无 `Caddy`、`docker-compose.internet`、`-Internet/--internet`、旧 `full/lan/prod/ops-check` Docker 入口残留引用。

## 0.1.24（2026-05-25）

### 海康告警链路性能与生命周期优化

- `backend/Aura.Api/Services/Hikvision/HikvisionAlertStreamHostedService.cs`：
  - 引入 `(deviceId → cameras)` 30 秒 TTL 缓存（`ConcurrentDictionary<long, CachedCameras>`），消除每帧图片缺通道号时回查 `CampusResourceRepository.GetCamerasByDeviceIdAsync` 造成的热路径 DB 命中。
  - 在 `ExecuteAsync` 的 `finally` 中清理缓存，避免后台服务停机时残留状态。
  - 将 `_ = Task.Run(() => RunDeviceLoopAsync(...), CancellationToken.None)` 改为 `_ = RunDeviceLoopAsync(...)`：循环本身第一步即 `await Task.Delay`/`CreateAsyncScope`，无需再用 `Task.Run` 占用线程池工作线程。
- `backend/Aura.Api/Services/Hikvision/HikvisionIsapiClient.cs`：
  - 4 个一次性调用入口（`GetStringAsync`、`GetBytesAsync`、`SendMultipartPostAsync`、`SendAsync`）由「每次请求 `new SocketsHttpHandler` + `new HttpClient(handler, disposeHandler: true)`」改为按 `(scheme, host, port, skipSsl, user, pwdHash)` 缓存的 `SocketsHttpHandler`，`HttpClient` 以 `disposeHandler: false` 复用底层连接池与 DNS 解析。
  - 长连接 `RunAlertStreamAsync` 继续使用独立 `CreateLongLivedHandler`，与短连接连接池解耦。
  - `BuildHandlerKey` 用 `password.GetHashCode(StringComparison.Ordinal)` 入键，避免明文凭据出现在缓存键或诊断日志。

### 后端列表端点 LIMIT 硬上限与生产配置加固

- `backend/Aura.Api/Data/CampusResourceRepository.cs`：暴露 `DefaultCampusNodeLimit/MaxCampusNodeLimit`、`DefaultFloorLimit/MaxFloorLimit`、`DefaultCameraLimit/MaxCameraLimit`，并在 `GetCampusNodesAsync`、`GetFloorsAsync`、`GetCamerasAsync` 中 `Math.Clamp` + `LIMIT @Limit` 强制约束。
- `backend/Aura.Api/Data/CaptureRepository.cs`：新增 `DefaultRoiLimit/MaxRoiLimit`，`GetRoisAsync` 接受 `limit` 参数并 clamp。
- `backend/Aura.Api/Extensions/AuraEndpointsCampusFloor.cs`、`AuraEndpointsDeviceCapture.cs`、`AuraEndpointsDomain.cs`：`/api/campus/tree`、`/api/floor/list`、`/api/camera/list`、`/api/roi/list` 端点改为读取 `?limit=` 参数并 clamp 到对应仓储常量，避免恶意大查询参数压垮数据库或内存。
- `backend/Aura.Api/Extensions/ServiceExtensions.cs` 的 `EnsureSafeProductionConfiguration` 新增两项校验：
  - `Ai:BaseUrl` 非空时必须为 HTTP/HTTPS 绝对 URI。
  - `Hikvision:Isapi:AlertStream:Enabled=true` 时必须配置 `DefaultUserName/DefaultPassword`（或对应环境变量入口），将「运行时仅记 warning 跳过」加固为「启动期立即失败」。

### 仓储层通用化与数据库索引补齐

- 新增 `backend/Aura.Api/Data/PgSqlRepositoryHelpers.cs`：抽出仓储中重复的「try { 数据库调用 } catch { 记日志返回 fallback }」样板，提供两个签名：
  - `ExecuteAsync<T>(factory, logger, operationLabel, operation, fallback, logLevel, logContext)`：查询型，失败返回 fallback。
  - `ExecuteVoidAsync(factory, logger, operationLabel, operation, logLevel, logContext)`：无返回值写入型，失败返回 `false`。
- `backend/Aura.Api/Data/MonitoringRepository.cs` 中 `InsertAlertWithTimeAsync`、`GetAlertsAsync`、`GetAlertCountAsync`、`GetAlertsInRangeAsync` 迁移到上述 helper，作为后续其他仓储增量迁移的样板。
- `backend/Aura.Api/Data/MonitoringRepository.cs`：`DefaultJudgeLimit` 由 `2000` 收敛为 `500`，与前端 `judge.js` 取消硬编码 `limit=2000` 后的实际加载量对齐，降低未指定参数时的默认成本。
- 新增 `database/migrations/006_add_map_camera_device_id_index.sql`：为 `map_camera(device_id)` 建立索引，覆盖 `GetCamerasByDeviceIdAsync` 在海康告警流通道回退时的过滤查询。
- `database/schema.pgsql.sql` 同步追加 `idx_map_camera_device_id`，`database/migrations/README.txt` 补充 `006` 说明。

### 前端公共请求迁移与默认 limit 收敛

- `frontend/track/track.js`：轨迹与摄像头列表加载改为优先调用 `window.aura.requestJson`，统一 `credentials: "include"`、JSON 解析与错误结构；不存在该工具时降级到原 `fetch` 兼容路径。`limit=500` 仍作为路径回放的合理上限保留（路径动画需完整序列）。
- `frontend/judge/judge.js`：
  - `post`/`load` 改为优先 `window.aura.requestJson`，与 `track.js` 一致。
  - 去掉硬编码 `&limit=2000`，使用后端新默认值 `500`（来自 `MonitoringRepository.DefaultJudgeLimit`），既减少首屏数据量也避免前后端默认值漂移。

### AI 检索指标失败原因维度与限流抽象

- `ai/services/index_runtime_service.py`：
  - `IndexRuntimeService` 新增 `_reason_stats: dict[tuple[str, str], int]`，`record_search` 按 `(status, reason)` 归一并累加（`status ∈ {success, empty, failed}`；reason 缺省时按状态归一为 `ok/no_hit/unknown`）。
  - `get_search_metrics()` 输出新增 `reasons: [{status, reason, count}]`，便于 `search-stats` JSON 直接展示。
  - `build_prometheus_metrics()` 新增 `aura_ai_search_reason_total{status, reason}` Counter，便于按失败原因切片告警与 PromQL 查询。
- `ai/routes/api_routes.py`：检索失败路径的 `reason` 由中文短句改为可枚举 ASCII（`internal_exception`、`index_unavailable`），与 Prometheus 标签兼容。
- `ai/app/middlewares.py` 新增 `RetrievalQuotaExceeded` 异常与对应 `@app.exception_handler`，统一输出 `{code: 42901, msg, request_id}` 响应体，保持与现有 429 契约一致。
- `ai/routes/api_routes.py`：将 `/ai/extract`、`/ai/extract-file`、`/ai/upsert`、`/ai/search`、`/ai/cluster` 五个路由内手动的 `_allow_operation(request)` 改为 `Depends(require_retrieval_quota)`，依赖函数命中阈值时抛 `RetrievalQuotaExceeded`，由全局处理器输出标准响应，路由内不再重复 `if not allowed: return blocked_response` 三行样板。
- `ai/tests/test_ai_hardening.py`：将检索异常用例的断言 `reason == "检索内部异常"` 同步改为 `reason == "internal_exception"`，与新枚举值对齐。

### 编辑器工作区配置

- `.vscode/settings.json`：新增 `files.associations`，将 `*.pgsql.sql` 与 `database/migrations/*.sql` 关联到 `postgres` 语言模式，避免 VS Code 内置 `mssql` 扩展把 PostgreSQL 文件按 T-SQL 解析后产生大量假阳性诊断（`CREATE EXTENSION`、`jsonb`、`gin_trgm_ops` 等）。不影响实际数据库与构建。

### Docker 化完整性补齐

- 复核所有 0.1.24 改动的 Docker 化路径：
  - 新增 `backend/Aura.Api/Data/PgSqlRepositoryHelpers.cs` 由 `backend.Dockerfile` 的 `COPY backend/Aura.Api/` 通配自动入镜像。
  - 新增 `database/migrations/006_add_map_camera_device_id_index.sql` 由 `backend/Aura.DbMigrator/Program.cs` 中 `^(?<version>\d+)_.*\.sql$` 正则自动识别，`db-migrate` 服务执行 `migrate` 时自动落地。
  - `database/schema.pgsql.sql` 中追加的 `idx_map_camera_device_id` 由 `pgsql` 服务的 `/docker-entrypoint-initdb.d/01-schema.pgsql.sql` 在新库首次初始化时生效。
  - AI 改动（`ai/app/middlewares.py`、`ai/routes/api_routes.py`、`ai/services/index_runtime_service.py`）由 `ai.Dockerfile` 的 `COPY ai /app/ai` 自动入镜像。
- `docker/docker-compose.prod.template.yml` 修复既有缺口：
  - `api` 服务补齐 `Ai__ApiKey`、`SignalR__RedisBackplane__Enabled/ConnectionString/ChannelPrefix` 四个环境变量透传，使 `.env.prod.example` 中已存在的相应变量真正生效（此前 0.1.23 引入的 SignalR Redis backplane 在 prod 模板未接通）。
  - `api` 服务新增 `healthcheck`：基于 `/api/health/live`（无鉴权、无外部依赖），与 `deploy/k8s/deployment-probes.example.yaml` 中 liveness probe 对齐。
  - `ai` 服务新增 `healthcheck`：基于 `/live`，与 `docker-compose.full.example.yml` 中 AI 探针对齐，补齐 prod 模板缺少的容器层就绪信号。
  - `ai` 服务环境补 `AURA_API_KEY`，与 `api` 的 `Ai__ApiKey` 形成强一致约束。
- `docker/.env.prod.example`：补 `AURA_API_KEY=` 注释行，与 `.env.full.example` 描述风格对齐，便于生产编排显式启用 AI API Key 校验。
- **Docker 与本机直跑环境分离**：`docker/build-images.ps1`、`docker/build-images.sh`、`docker/up-full.ps1`、`docker/up-full.sh` 由读根目录 `.env` 改为读 **`.env.docker`**：
  - 根目录 `.env`：服务于本机直跑（`start_services.py` / `dotnet run` / `uvicorn`），变量沿用 .NET 配置风格（`Jwt__Key`、`ConnectionStrings__PgSql`），host 为 `127.0.0.1`。
  - `.env.docker`：服务于 Docker compose 编排（由 `docker/.env.full.example` 复制而来），变量沿用 compose 取值风格（`JWT_KEY`、`POSTGRES_PASSWORD`），host 指向容器名 `pgsql`/`redis`/`arangodb`。
  - 两份文件互不覆盖，避免混用导致连接串或鉴权失败；`.env.docker` 由 `.gitignore` 的 `.env.*` 通配规则自动忽略。
  - `down-full.*` 与 `check-full.*` 本就不读 env，无需调整；`deploy-aura-ubuntu.sh` 为生产 Ubuntu 一键部署脚本，在远端部署目录自生成 `.env`，独立于开发者本机配置，不在此次调整范围内。
  - `docker/README.md` 「Full 示例使用」章节同步更新使用约定并补充「仅构建镜像」步骤。

### 验证记录

- 已通过：
  - `dotnet build backend\Aura.Api\Aura.Api.csproj`：0 警告 0 错误。
  - `dotnet test backend\Aura.Api.Integration.Tests\Aura.Api.Integration.Tests.csproj --no-build`：42/42 通过。
  - `pytest ai\tests -q`：11/11 通过。
  - `npm run lint`（在 `frontend/`）：通过。
  - `docker-compose --env-file docker\.env.full.example -f docker\docker-compose.full.example.yml config`：编排合法。
  - `docker-compose --env-file docker\.env.prod.example -f docker\docker-compose.prod.template.yml config`：编排合法，`Ai__ApiKey`、`SignalR__RedisBackplane__*`、`healthcheck` 透传渲染正确。
- 未覆盖项（按当前沙箱限制）：
  - 数据库 `006` 迁移仅做语法准备，需在目标 PostgreSQL 上执行 `dotnet run --project backend/Aura.DbMigrator -- migrate` 完成落地。
  - 海康告警流 N+1 优化与 ISAPI Handler 缓存的真实收益依赖具备 NVR/抓拍硬件的联调环境观测，可在压测脚本基础上对比 `aura_ai_search_reason_total` 与设备出口 QPS。

## 0.1.23（2026-05-18）

### AI 健康探针与启动就绪语义拆分

- `ai/routes/api_routes.py` 新增统一健康载荷构造，并补齐：
  - `GET /live`：仅表达 AI 服务进程存活，适合容器/Kubernetes liveness probe。
  - `GET /ready`：表达模型、向量库与运行态就绪，适合 readiness probe 与发布门禁。
  - `GET /` 继续保留兼容行为，但内部复用同一健康载荷，避免多处状态语义漂移。
- `ai/app/middlewares.py` 放行匿名 `GET/HEAD /live` 与 `/ready`，避免探针依赖业务 API Key。
- `start_services.py` 调整本机启动等待逻辑：先等待 `/live` 存活，再等待 `/ready` 且 `model_loaded=true`，避免服务进程已起但模型尚未就绪时误判成功。
- `docker/check-full.ps1`、`docker/check-full.sh`、`docker/deploy-aura-ubuntu.sh` 同步拆分 AI `/live` 与 `/ready` 巡检。
- `ai/tests/test_ai_routes_and_index.py` 补齐 `/live`、`/ready` 回归覆盖；`pytest.ini` 增加 `asyncio_default_fixture_loop_scope=function`，收敛 pytest-asyncio fixture loop 警告。

### 后端依赖升级、SignalR 扩展与生产配置校验

- `backend/Aura.Api/Aura.Api.csproj` 安全升级依赖包：
  - `Microsoft.Extensions.Http.Resilience`、`BCrypt.Net-Next`、`Dapper`、`Microsoft.AspNetCore.*`、OpenTelemetry、`StackExchange.Redis` 等升级到当前兼容版本。
  - 保持 `Npgsql` 在 8.x 线，避免跨大版本升级带来运行时兼容风险。
- 新增 `Microsoft.AspNetCore.SignalR.StackExchangeRedis`，并在 `ServiceExtensions` 中支持可选 `SignalR:RedisBackplane`：
  - 默认关闭，不影响单实例部署。
  - 开启后可复用 Redis 连接串，为多实例 SignalR 事件广播做准备。
- `ServiceExtensions` 增加生产配置校验：
  - 生产环境要求显式配置 `AllowedHosts`。
  - 对可选告警 Webhook 配置做 URL 合法性校验，降低上线后才暴露配置错误的概率。
- `.env.example`、`docker/.env.full.example`、`docker/.env.prod.example`、`appsettings*.json` 同步补齐 SignalR、AllowedHosts 与相关生产配置项。

### 数据库查询优化与迁移脚本

- `database/schema.pgsql.sql` 新增查询索引：
  - `idx_track_event_vid_time_desc`
  - `idx_capture_device_time_image`
  - `idx_capture_feature_time_image`
- 新增 `database/migrations/005_add_capture_track_lookup_indexes.sql`，用于存量库增量落地上述索引。
- `database/migrations/README.txt` 补充 `005` 迁移说明。
- `CaptureRepository.GetBestCaptureImageByVidsAsync` 优化轨迹匹配抓拍图片查询：
  - 由“全量候选排序取最近”改为分别取事件前后最近候选再比较。
  - 降低 `ABS(EXTRACT(...)) ORDER BY` 对大表扫描与排序的压力。

### Docker 镜像与数据库迁移闭环

- `docker/backend.Dockerfile` 同时发布 `Aura.Api` 与 `Aura.DbMigrator`：
  - 运行镜像内新增 `/app/migrator/Aura.DbMigrator.dll`。
  - 同步复制 `Aura.sln` 与 `database/`，保证迁移工具能读取迁移脚本。
- `docker/docker-compose.full.example.yml` 与 `docker/docker-compose.prod.template.yml` 新增 `db-migrate` 服务：
  - API 服务改为依赖 `db-migrate: service_completed_successfully`。
  - 避免应用启动时数据库 schema 尚未完成迁移。
- Docker full/prod 模板中的 AI healthcheck 改为 `/live`，将容器存活检查与模型就绪检查解耦。
- `docker/README.md` 补充 `db-migrate` 服务说明与生产部署注意事项。

### 前端分页、公共请求与列表防护

- `frontend/capture/capture.js` 将抓拍列表从固定 `limit=500` 改为服务端分页：
  - 请求 `/api/capture/list?page=...&pageSize=...`。
  - 新增服务端 pagination 解析与兼容本地分页回退逻辑。
  - 大数据量抓拍场景下减少首屏数据传输与浏览器渲染压力。
- `frontend/common/shell.js` 新增 `window.aura.requestJson(url, options)`：
  - 统一 `credentials: "include"`、JSON body 序列化、JSON/text 响应解析。
  - 先接入全站会话检查与公共导出能力，保持其它页面行为兼容。
- `MonitoringRepository` 与 `AuraEndpointsDomain` 对告警、研判、轨迹列表补充服务端 limit 硬上限，避免异常大查询参数造成数据库或内存压力。

### Kubernetes 运维样例补齐

- 新增 `deploy/k8s/deployment-probes.example.yaml`：
  - API：`/api/health/live` 作为 liveness/startup probe，`/api/health` 作为 readiness probe。
  - AI：`/live` 作为 liveness/startup probe，`/ready` 作为 readiness probe。
- `deploy/k8s/README.md` 更新为“探针、指标与网络”说明，并明确：
  - `/api/ops/readiness` 适合发布门禁或运维巡检。
  - 该端点需要管理员认证，不适合作为 Kubernetes 原生探针直接调用。

### 文档与运维手册同步

- `README.md` 更新 AI 启动与健康检查说明，从根路径探测调整为 `/live` + `/ready` 双阶段。
- `docs/部署文档与运维手册.md`、`docs/AI生产完整性检查清单.md`、`docs/readiness运维使用说明.md` 同步补充 AI live/ready 语义。
- Docker 示例环境文件、生产模板与检查脚本同步补齐本次新增配置与探针路径。

### 验证记录

- 已通过：
  - `npm run lint`
  - `dotnet test backend\Aura.Api.Tests\Aura.Api.Tests.csproj --no-build -v minimal`（9 个测试通过）
  - `dotnet test backend\Aura.Api.Integration.Tests\Aura.Api.Integration.Tests.csproj --no-restore -v minimal`（此前完整验证 42 个测试通过）
  - `pytest ai\tests`（此前完整验证 11 个测试通过）
  - `docker-compose --env-file docker\.env.full.example -f docker\docker-compose.full.example.yml config`
  - `docker-compose --env-file docker\.env.prod.example -f docker\docker-compose.prod.template.yml config`
  - `dotnet list backend\Aura.Api\Aura.Api.csproj package --vulnerable --include-transitive`（未发现易受攻击包）
- 受当前本机沙箱/文件锁限制：
  - 完整 `dotnet test` 重编译在默认 `obj` 写入阶段可能遇到 `Access to the path ... is denied`，需在可写构建环境或释放占用进程后复跑。
  - 最新一次 `pytest ai\tests` 复跑受临时目录权限影响未完成；此前提升权限运行已通过。

## 0.1.21（2026-04-22）

### AI 链路可靠性与状态语义收敛

- 新增 `backend/Aura.Api/Ai/AiMetadataComposer.cs`，统一生成抓拍元数据中的 AI 字段，补齐并标准化以下状态：`ai_status`、`ai_vector_success`、`ai_vector_msg`、`ai_vector_engine`、`ai_retry_queued`、`ai_retry_reason`。
- `CaptureProcessingService` 改为“提特征 + 向量写入”双阶段状态机：向量写入失败会进入补偿队列并写入明确失败原因；成功后根据配置清理临时图片，避免无效文件残留。
- `RetryProcessingService` 增强向量补偿分支：重试任务除提特征外，新增向量落库失败的重排队逻辑、失败兜底与抓拍元数据回写；并在数据库不可更新时回退更新内存态，降低状态漂移。
- `CaptureRepository` 新增 `UpdateCaptureFeatureIdAsync`，在向量 ID 可用时及时回写 `capture_record.feature_id`，支撑后续检索与审计对齐。

### AI 客户端与检索可观测增强

- `AiClient` 返回模型升级：
  - `SearchAsync`、`UpsertAsync` 由“裸 bool/列表”改为结构化结果（成功标记 + 消息 + 引擎信息），错误可携带 HTTP 状态与业务 code。
  - 新增 `GetSearchStatsAsync(windowMinutes)`，对接 `/ai/search-stats`，用于运维面板读取检索失败率与平均延迟。
  - 增强 JSON 解析容错与失败消息构造，降低“HTTP 200 但业务失败”时的误判。
- `VectorApplicationService` 接入新的 `AiSearchResult` 语义，AI 检索失败时返回网关错误而非空列表，避免前端把失败误当“无结果”。
- AI Python 侧同步收敛：
  - `ai/routes/api_routes.py`：提特征异常返回显式 HTTP 500；缺失文件返回 HTTP 404（保留业务 code）。
  - `ai/services/index_runtime_service.py`：仅统计“成功且 0 命中”为 empty，修复失败请求误计为空结果的问题。
  - `ai/vector_store/index_store.py`：桶探针与 explain/meta 字段更一致，补充 `ann_probe/requested_ann_probe/rerank_window` 并明确策略名。

### 统计看板与首页态势联动升级

- 后端统计（`StatsApplicationService`）新增 AI 运维汇总：
  - `GET /api/stats/overview` 新增 `data.ai`，包含 `AI失败率/补偿队列/向量异常/检索失败率/检索延迟` 等指标。
  - `GET /api/stats/dashboard` 新增 `aiStatus`（状态分布）与 `aiDaily`（链路趋势）两组图表数据。
- 统计页（`frontend/stats/*`）重做为“概览 KPI + 图表面板”：
  - 新增 AI 运维 KPI 区（失败率、补偿队列、向量异常、检索失败率、检索延迟）。
  - 新增 `AI状态分布` 与 `AI链路趋势` 两张图，图表诊断/重试逻辑同步覆盖新增容器。
  - 布局改为紧凑化并支持统计页主内容纵向滚动，避免下方图表被首屏截断。
- 首页态势（`frontend/index/*`）接入 AI 运维提醒：
  - 新增 AI 指标卡与异常列表。
  - 顶部“系统状态”新增 `AI链路风险` 文案与风险等级（低/中/高）。
  - 新增风险升级 toast（仅升级触发），并加入 5 分钟冷却；冷却时长支持通过 `window.AURA_PAGE_CONFIG.aiRiskToastCooldownMs` 配置。

### 抓拍与日志页面可读性优化

- 抓拍页（`frontend/capture/*`）：
  - 元数据列改为结构化展示（状态徽标、摘要、向量信息、补偿说明、原始元数据折叠查看）。
  - 图片路径改为可点击链接，空路径时显示“未归档”占位。
- 日志页（`frontend/log/*`）：
  - 操作日志与系统日志表格改为“标签 + 详情”结构，减少低价值字段堆叠。
  - 新增日志标签（异常/关注/AI/向量/重试）与详情单元样式，提升排障扫描效率。

### 测试补齐

- 新增 `backend/Aura.Api.Tests/AiClientTests.cs`，覆盖 AI 客户端在“HTTP 成功但业务失败”与 `search-stats` 解析等关键路径。
- 新增 `backend/Aura.Api.Integration.Tests/StatsEndpointTests.cs`，覆盖统计接口中 AI 运维字段与图表载荷结构。
- 新增 `ai/tests/test_ai_routes_and_index.py`，覆盖提特征异常状态码、缺文件状态码、检索 empty 统计口径与桶探针 explain 语义。

## 0.1.22（2026-04-22）

### AI 稳定性与安全加固

- `ai/app/bootstrap.py`：新增环境变量安全解析（整数/浮点容错回退），避免非法值在导入期触发 `ValueError` 导致服务无法启动。
- `ai/app/lifespan.py`、`ai/services/inference_service.py`：补齐后台初始化任务与推理批处理循环的优雅停机流程（任务句柄管理、取消与回收），降低进程退出时的悬挂任务风险。
- `ai/utils/service_state.py`：健康状态新增诊断开关 `AURA_AI_HEALTH_VERBOSE`；生产默认不返回 `arango_error`/`model_error` 详细信息，减少内部异常暴露面。
- `ai/routes/api_routes.py`：`/ai/search` 增加总兜底异常处理并补齐失败审计记录，未预期异常统一返回脱敏消息（`code=50002`）。
- `ai/routes/api_routes.py`：`/ai/extract-file` 新增目录白名单约束（`AURA_AI_EXTRACT_FILE_ROOTS`），越界访问返回 `40301`。
- `ai/routes/api_routes.py`：将限流保护扩展到 `/ai/extract`、`/ai/extract-file`、`/ai/upsert`、`/ai/search`、`/ai/cluster` 全部重负载路由。
- `ai/models/schemas.py`：为请求模型补充长度与范围约束（如 `feature/top_k/ann_probe/max_vectors/vid`），前移输入校验边界。

### 配置与测试补齐

- `.env.example`：新增 AI 背压与安全相关键（`AURA_AI_INFER_*`、`AURA_AI_HEALTH_VERBOSE`、`AURA_AI_EXTRACT_FILE_ROOTS`）说明。
- `pytest.ini`：统一 `pytest` 导入路径与测试目录，降低测试对工作目录的依赖。
- 新增 `ai/tests/test_ai_hardening.py`，覆盖环境变量容错、生产健康脱敏与检索异常兜底审计分支。
- `ai/tests/test_ai_routes_and_index.py` 补充路径白名单拒绝与新限流依赖桩，覆盖新增安全逻辑。

## 0.1.20（2026-04-21）

### AI 检索可观测与巡检增强（同日增量）

- AI 健康检查 `GET /` 增加三类可视化字段：`熔断状态`、`限流状态`、`回填状态`，并同步保留结构化对象 `retrieval_guard`、`backfill_state` 便于程序解析。
- 新增检索审计日志接口 `GET /ai/search-audit-logs`，返回结构化 JSON（含 `request_id`、`status`、`reason`、`latency_ms`、`engine`、`warnings` 等），用于快速定位失败与慢请求。
- 新增 AI 检索巡检脚本 `AI检索巡检脚本.ps1`：
  - 默认模式输出中文巡检结论与问题清单；
  - CI 模式支持 `-JsonOutput`，仅输出结构化 JSON，退出码保持 `0=通过`、`2=未通过`、`3=执行异常`。
- 新增运行时服务文件 `ai/services/index_runtime_service.py`、`ai/services/retrieval_guard_service.py` 与配置工具 `ai/utils/retrieval_config.py`，统一沉淀检索指标、审计记录、熔断/限流状态及参数纠偏逻辑。
- 文档同步更新：
  - `README.md` 补充 AI 健康字段、审计日志接口与巡检脚本用法；
  - `docs/部署文档与运维手册.md` 补充巡检清单、字段解释与响应示例，便于值班与发布前排查。
- 规范更新：`开发规范.md` 新增“所有新建代码文件必须添加文件头注释（中文名 + 英文名）”规则。

### AI 服务结构收敛（同日增量）

- `ai/main.py` 进一步收敛为应用装配入口：保留 `create_app()` 与 `app` 导出，不再承载具体路由实现与中间件细节。
- 新增 `ai/routes/`：将健康检查、特征提取、检索、写入、聚类等接口从入口文件拆分到独立路由模块，降低主文件复杂度。
- 新增 `ai/app/`：补齐启动装配与生命周期分层（`bootstrap.py`、`lifespan.py`、`middlewares.py`、`route_deps.py`），统一管理运行时依赖与装配逻辑。
- `ai/storage/` 更名为 `ai/vector_store/`，并同步更新导入路径，避免与仓库根目录 `storage/` 重名造成混淆；旧目录已清理。
- 本次改动为结构性重构，不改变 AI 既有 API 路径与对外行为（保持向后兼容）。

## 0.1.19（2026-04-21）

### 本次说明

- 本次为“数据库迁移工具化 + 数据访问层拆分 + 统一错误响应 + 安全扫描与回归测试补齐”的综合迭代，覆盖后端、数据库、AI、前端与 CI。
- 变更以兼容存量环境为前提：新增增量 SQL 迁移脚本与 `Aura.DbMigrator`，并将“运行时修复 identity 序列”的行为迁移为显式迁移步骤，便于上线可控。

### CI / 安全基线（GitHub Actions）

- 新增安全扫描工作流：
  - CodeQL：代码静态分析。
  - Gitleaks：敏感信息泄露扫描。
  - Trivy：依赖与镜像漏洞扫描（按仓库策略执行）。

### 数据库 · 增量迁移与可控执行

- **迁移脚本目录补齐**：新增 `database/migrations/001..004_*.sql`，用于对存量库做字段/表/索引与序列同步修复（基线仍以 `database/schema.pgsql.sql` 为准）。
- **迁移工具**：新增 `backend/Aura.DbMigrator`：
  - `status`：查看已应用/待应用脚本与校验和一致性。
  - `migrate`：按版本顺序应用待执行脚本，并记录到 `schema_migrations`。
  - `bootstrap`：仅空库可用；先应用 `schema.pgsql.sql`，再将当前增量脚本登记为 baseline，统一迁移历史。
- **运行时行为调整**：从 `003_sync_identity_sequences.sql` 起，应用不再在运行时修复 `sys_role/sys_user` 的 identity 序列；升级时需先执行对应迁移脚本（详见 `database/migrations/README.txt`）。

### 后端 · 数据访问层拆分与统一错误响应

- **数据访问层拆分**：新增 `backend/Aura.Api/Data/*Repository.cs`、`PgSqlConnectionFactory.cs`、`PgSqlRecords.cs`、`UserQueryService.cs` 等，将原 `PgSqlStore` 的职责拆分为更明确的仓储与查询服务，降低超大文件维护成本并便于后续单测/集测覆盖。
- **统一错误响应模型**：新增 `ApiErrorResponse` 与 `AuraApiResults`，用于 Minimal API 与中间件统一输出结构化 JSON 错误（`code/msg/data/traceId`），避免前端在不同错误形态间解析不稳定。
- **全局异常处理与鉴权链路**：`GlobalExceptionHandlerExtensions`、端点扩展与相关服务做了配套更新，以对齐统一错误返回与新数据访问层。

### 修复与维护性

- **Testing 配置合法化**：移除 `backend/Aura.Api/appsettings.Testing.json` 中的 `//` 注释，避免 JSON 解析/校验器报错（JSON 标准不支持注释）。
- **测试警告清零**：修复 `xUnit1031/xUnit2013` 分析器警告（测试改为 `async/await`、集合空断言改为 `Assert.Empty`），确保 `dotnet build` 在仓库默认规则下无警告通过。

### AI · 严格模式与测试补齐

- 新增 AI 侧开发依赖清单 `ai/requirements-dev.txt`，并补齐 `pytest` 用例（`ai/tests/test_main.py`）：
  - 覆盖健康检查、特征提取、检索回退与“严格模式（要求 Arango 可用）”下的 503 行为与拒绝内存回退策略。

### 前端 · 冒烟测试与工程约束

- 新增 Playwright 冒烟测试框架与用例（`frontend/tests/smoke/*`），并提供 `frontend/playwright.smoke.config.js`：
  - 本地默认优先使用系统 Chrome（减少缺少 ffmpeg/浏览器依赖导致的阻塞），CI 继续使用 Playwright 安装浏览器。
- 工程侧配套更新：`frontend/package.json` 增加 `lint/smoke` 脚本，`frontend/eslint.config.cjs` 与锁文件同步更新。

### 按文件落点（审计清单）

- **CI / 安全扫描**：
  - 新增：`.github/workflows/codeql.yml`、`.github/workflows/gitleaks.yml`、`.github/workflows/trivy.yml`
  - 修改：`.github/workflows/dotnet-ci.yml`
- **后端（`backend/Aura.Api`）**：
  - 修改：`Program.cs`、`Extensions/*`、`Middleware/*`、`Services/Hikvision/*`、`Capture/*`、`Export/*`、`Clustering/*`、`*ApplicationService.cs`、`DeviceManagementService.cs`、`IdentityAdminService.cs`、`JudgeService.cs`、`ResourceManagementService.cs`、`RetryProcessingService.cs`、`MonitoringQueryService.cs`、`OperationQueryService.cs`、`OutputApplicationService.cs`、`SystemLogQueryService.cs`、`VectorApplicationService.cs`、`SpaceCollisionService.cs`
  - 修改：`Data/PgSqlStore.cs`
  - 新增：`Data/AuditRepository.cs`、`Data/CampusResourceRepository.cs`、`Data/CaptureRepository.cs`、`Data/DeviceRepository.cs`、`Data/MonitoringRepository.cs`、`Data/PgSqlConnectionFactory.cs`、`Data/PgSqlRecords.cs`、`Data/UserAuthRepository.cs`、`Internal/AuraApiResults.cs`、`Models/ApiErrorResponse.cs`、`UserQueryService.cs`
  - 配置修改：`appsettings.json`、`appsettings.Development.json`、`appsettings.Production.json`、`appsettings.Testing.json`
- **后端测试**：
  - 修改：`backend/Aura.Api.Integration.Tests/HikvisionIsapiOptionsValidatorTests.cs`、`backend/Aura.Api.Integration.Tests/PasswordChangeEnforcementTests.cs`
  - 新增：`backend/Aura.Api.Integration.Tests/UnifiedErrorResponseTests.cs`、`backend/Aura.Api.Integration.Tests/UserPaginationTests.cs`
  - 修改：`backend/Aura.Api.Tests/Aura.Api.Tests.csproj`
  - 删除：`backend/Aura.Api.Tests/Program.cs`
  - 新增：`backend/Aura.Api.Tests/ClusteringTests.cs`、`backend/Aura.Api.Tests/HikvisionAlertStreamMultipartParserTests.cs`、`backend/Aura.Api.Tests/TabularExportServiceTests.cs`
- **数据库**：
  - 修改：`database/schema.pgsql.sql`、`database/migrations/README.txt`
  - 新增：`database/migrations/001_ensure_sys_user_columns.sql`、`002_ensure_log_system_table.sql`、`003_sync_identity_sequences.sql`、`004_add_log_search_trgm_indexes.sql`
- **数据库迁移工具**：
  - 新增：`backend/Aura.DbMigrator/`（`Aura.DbMigrator.csproj`、`Program.cs`）
- **AI**：
  - 修改：`ai/main.py`
  - 新增：`ai/requirements-dev.txt`、`ai/tests/test_main.py`
- **前端**：
  - 修改：`frontend/common/shell.js`、`frontend/device/vendors/hik-isapi-actions.js`、`frontend/index/index.js`、`frontend/scene/scene.js`、`frontend/user/user.js`
  - 修改：`frontend/package.json`、`frontend/package-lock.json`、`frontend/eslint.config.cjs`
  - 新增：`frontend/playwright.smoke.config.js`、`frontend/tests/smoke/server.js`、`frontend/tests/smoke/smoke.spec.js`
  - 产物：`frontend/test-results/.last-run.json`（测试输出，是否纳入版本管理以仓库策略为准）
- **部署/脚本与模板**：
  - 修改：`.env.example`、`Aura.sln`、`docker/.env.prod.example`、`docker/deploy-aura-ubuntu.sh`、`docker/docker-compose.prod.template.yml`

## 0.1.18（2026-04-20）

### 安全 · 强制改密闭环（后端 + 前端）

- **会话态新增“需改密”语义**：
  - 后端新增 Claim：`aura:must_change_password`（见 `AuraHelpers.MustChangePasswordClaimType`），登录态与后续鉴权链路可携带该标记。
  - `GET /api/auth/me` 返回体增加 `mustChangePassword`，便于前端在不额外请求的前提下判断是否需要跳转改密页。
- **新增改密 API**：`POST /api/auth/change-password`（需登录态），校验当前密码、校验新密码强度（至少 12 位且包含大小写/数字/特殊字符），成功后更新密码并清除“需改密”标记，同时刷新会话 Cookie。
- **强制拦截策略**：新增 `PasswordChangeEnforcementMiddleware` 并在管道中启用；当账号被标记为“需改密”时：
  - 允许的最小路径白名单：`/api/auth/me`、`/api/auth/logout`、`/api/auth/change-password`、`/api/health`、`/api/health/live`
  - 对其余 API/Hub 请求返回 `403`（`code=40321`，中文提示“当前账号需要先修改密码后才能继续使用”），避免在未改密时继续操作系统能力。
- **新增改密页**：新增 `frontend/password/`（`password.html/.css/.js`），全程 `credentials: "include"` 使用同源 HttpOnly Cookie；支持携带 `returnUrl`，改密成功后回跳；并提供“一键退出登录”。
- **登录页跳转逻辑**：`frontend/login/login.js` 登录成功后读取 `mustChangePassword`，若为 `true` 则优先跳转至 `/password/?returnUrl=...`，避免用户进入系统后才遇到 403 阻断。
- **全站壳层兜底跳转**：`frontend/common/shell.js` 在加载会话（`/api/auth/me`）后，若发现 `mustChangePassword=true` 则对非改密页做 `window.location.replace` 跳转，避免用户从历史书签/刷新进入其它页面后频繁遇到 403。

### 用户管理 · 密码重置与安全提示

- **后端用户域补齐字段**：`sys_user` 增加 `must_change_password`（自动 `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` 确保兼容存量库），并在登录查询与用户列表中透出该字段；`DbUser`、`DbUserListItem` 与 `UserEntity` 同步增加 `MustChangePassword`。
- **管理员重置密码**：支持“指定新密码”或“自动生成一次性临时密码”两种模式；重置后强制用户下次登录先改密（`must_change_password=true`）。
- **前端用户页展示**：`frontend/user/user.js` 用户角色旁新增“需改密”标签（样式 `frontend/user/user.css`），重置密码成功提示改为展示“临时密码”（仅在本次操作结果中可见）；导入模板示例密码调整为更安全的示例值（`TempPass#2026`）。

### 兼容性与工程改动

- **生产配置更安全**：
  - `backend/Aura.Api/appsettings.Production.json` 将 `ConnectionStrings:PgSql/Redis` 改为 `PLEASE_SET_*` 占位，避免误把示例弱口令带入生产。
  - `backend/Aura.Api/appsettings.Production.json` 默认关闭 `Ops:Metrics:ExposePrometheus`，降低误暴露指标端点风险（生产建议按网络/反向代理策略显式开启）。
- **默认 CSP 收紧**：`backend/Aura.Api/Program.cs` 默认 `Content-Security-Policy` 的 `script-src` 去掉 `'unsafe-eval'`，避免默认放开不必要的执行能力；如确有业务需要，可继续通过 `Security:CspPolicy` 显式覆盖。
- **Testing 配置补齐**：`backend/Aura.Api/appsettings.Testing.json` 补齐 `Hikvision:Isapi:AlertStream` 段（默认关闭），保证测试环境配置结构与主配置一致。
- **本机启动脚本（端口占用判定与清理策略）**：`start_services.py` 端口占用检测仅将 `LISTENING` 视为“端口被占用”，避免 `TIME_WAIT/ESTABLISHED` 等误判阻断一键启动；并默认对 **8000（AI）** 做一次安全清理以降低残留监听导致的启动失败概率；**5001（.NET）** 仍保持谨慎策略，仅在显式 `--kill-conflicts` 时清理。
- **数据访问补充**：`PgSqlStore` 增加按设备查询摄像头列表 `GetCamerasByDeviceIdAsync`，便于后续设备联动场景复用。
- **回归脚本修复**：`抓拍链路回归脚本.ps1` 修复 `$null` 比较告警（`if ($null -ne $Body)`），符合 PSScriptAnalyzer 推荐写法。
- **工程配置**：`backend/Aura.Api/Aura.Api.csproj` 补充 `Microsoft.AspNetCore.OpenApi.Generated` 拦截命名空间配置，便于 OpenAPI 相关源生成/拦截器协同工作。
- **补齐导出页目录**：新增 `frontend/export/export.html`、`export.js`、`export.css`，修复 `frontend/export/` 为空导致文档入口不可用的问题；页面复用 `window.aura.exportDataset()`（`frontend/common/shell.js`）执行导出。
- **设备联调页按钮可辨识度增强**：`frontend/device-diag/device-diag.css` 为“执行型/说明型”操作按钮统一增加角标（“执/说”）与边框差异，降低仅靠颜色区分带来的误触风险。

### 海康告警流 · 图片入库与稳态增强

- **告警流图片部件接入抓拍闭环**：`HikvisionAlertStreamHostedService` 支持将 `image` 部件按配置写入既有抓拍处理链路（入库 → AI → 向量 → 告警 → 重试 → 事件推送），避免“告警有图但不进入抓拍主链路”的割裂。
- **乱序回填与通道号稳态**：
  - `HikvisionAlertStreamXmlInterpreter` 增加通道号与事件时间提取（兼容多字段名），并将“接收时间”作为稳态基准避免设备时钟漂移误配。
  - 对每设备维护最近 N 条 XML 事件窗口，支持 image 先到时短暂等待 `ImageWaitForRecentXmlMs` 以回填；仍缺通道号时可按配置从摄像头布点表回退选择通道（策略 `first/latest`）。
- **安全与去噪**：新增图片大小上限 `MaxImageBytes`、重复图片去重窗口 `DedupWindowSeconds`，超限/重复将丢弃并计数，避免异常大包与重复风暴。
- **配置项**：`Hikvision:Isapi:AlertStream` 新增 `IngestCaptureEnabled/MaxImageBytes/AllowCameraChannelFallback/CameraChannelFallbackStrategy/DedupWindowSeconds/XmlRecentCacheSize/XmlRecentCacheTtlSeconds/ImageWaitForRecentXmlMs`，并在 `HikvisionIsapiOptionsValidator` 增加范围校验。

### 测试

- 新增集成测试用例：
  - `backend/Aura.Api.Integration.Tests/PasswordChangeEnforcementTests.cs`
  - `backend/Aura.Api.Integration.Tests/HikvisionAlertStreamRegistryRecentEventsTests.cs`
  - `backend/Aura.Api.Integration.Tests/HikvisionAlertStreamXmlInterpreterTests.cs`

## 0.1.17（2026-04-20）

### P2 能力扩展（事件长链与媒体分层）

- **海康 alertStream 后台订阅**：配置 `Hikvision:Isapi:AlertStream.Enabled=true` 且具备默认设备凭据时，`HikvisionAlertStreamHostedService` 对登记的海康 ISAPI 设备维持 `GET /ISAPI/Event/notification/alertStream` 长读；按官方 Demo 语义解析 `multipart/mixed`，跳过 `eventState=inactive`，订阅应答与事件 XML 摘要经 SignalR **`hikvision.alertStream`** 推送给楼栋管理员/超级管理员分组。新增指标 **`aura_hikvision_alert_stream_parts_total`**（按部件类型聚合，不含设备维度）。
- **告警流乱序稳态增强**：补齐“image 部件可能先于 XML 到达”的极端顺序处理能力。后端对每设备维护**最近 N 条 XML 事件缓存**并设置 TTL 淘汰，image 到达时优先在窗口内择优回填（优先选择最新且带通道号的事件）；当暂时缺少可用 XML 时支持按配置**短暂等待最近事件**后再回填，避免乱序导致通道号缺失/元数据不完整。新增配置：`XmlRecentCacheSize`、`XmlRecentCacheTtlSeconds`、`ImageWaitForRecentXmlMs`；并补充对应单元测试覆盖。
- **媒体规划 API**（不代理码流）：`GET /api/media/capabilities`、`POST /api/media/hikvision/stream-hint`（与抓图相同的通道号/码流类型规则生成 `StreamingChannelId`，返回典型 RTSP 路径模板，**不返回口令**）。
- **前端设备页**：经本地 `signalr-vendor-loader.js` 加载 SignalR，展示长连接推送；增加「媒体能力说明」「RTSP 路径提示」按钮。
- **可观测性**：`HikvisionAlertStreamRegistry` 记录各设备阶段（connecting / streaming / reconnecting / error）与最近事件时间；**`GET /api/device/hikvision/alert-stream-status`**（楼栋管理员）返回当前配置与进程内状态。`start_services.py` 成功启动后打印启用提示。**Aura.Api.Tests** 增加 multipart 单段解析自检。

### 前端 · 设备管理 / 海康联调 UI 与脚本结构

- **独立联调页**：新增 `frontend/device-diag/`（`device-diag.html` / `device-diag.css` / `device-diag.js`），与设备列表分区并列展示；`frontend/common/shell.js`、`shell.css` 补充导航入口与壳层样式衔接。
- **流媒体通道行布局**：`frontend/device/device.html` 与联调页中，「流媒体通道号（请求关键帧，可空）」使用 **`hik-isapi-field-grid-full`** 占满当前表单网格整行；「连通性探测」「设备信息」「ISAPI 抓图」「Demo 对照目录」经 **`hik-isapi-stream-input-row`** 排在输入框**右侧同一行**，**`hik-isapi-actions-inline`** 保持横向不换行。输入区域与按钮区域采用两列网格：**`minmax(min(100%, calc(var(--form-file-input-basis) + 2rem)), 1fr) max-content`**，并略收紧列间距，优先保证输入区宽度与占位提示可视性；输入框 **`title`** 与占位符文案一致，便于悬停查看全文。
- **样式**：`frontend/device/device.css` 扩充海康诊断面板、网关与流媒体行等规则；`frontend/device-diag/device-diag.css` 对联调页使用更紧凑的间距，并为流媒体行内输入框统一高度与字号。
- **脚本拆分**：`frontend/device/device.js` 重构；海康 / 大华 / ONVIF 及诊断厂商调度等逻辑迁至 `frontend/device/vendors/`（如 `hik-isapi-actions.js`、`diag-vendors.js` 等）。
- **全局表单与周边页**：`frontend/common/forms.css` 表单与控件展示规则补充；`frontend/capture/capture.html`、`capture.js` 小步调整；`frontend/role/role.html`、`role.js` 微调。

### 后端 · 媒体路由、ISAPI 与中间件（同日工作区合并）

- **媒体能力路由**：新增 `backend/Aura.Api/Extensions/AuraEndpointsMedia.cs`，并在 `EndpointExtensions`、`ServiceExtensions` 中完成注册（与上文「媒体规划 API」一致）。
- **告警长链实现文件**：`HikvisionAlertStreamHostedService`、`HikvisionAlertStreamMultipartParser`、`HikvisionAlertStreamRegistry`、`HikvisionAlertStreamXmlInterpreter` 等（与上文 alertStream 能力一致，落地于 `Services/Hikvision/`）。
- **其它增量**：`HikvisionIsapiClient.cs` 能力扩展；`HikvisionNvrIntegrationService.cs`、`HikvisionIsapiDemoCatalog.cs`、`HikvisionIsapiMetrics.cs`、`HikvisionIsapiOptions.cs`、`HikvisionIsapiOptionsValidator.cs`、`AuraEndpointsHikvisionIsapi.cs`、`Requests.cs`、`appsettings.json` 等随联调与媒体能力迭代；`FrontendRoutingMiddleware.cs` 前端路由衔接；`Aura.Api.csproj`、`backend/Aura.Api.Tests/Program.cs` 随集成与自检更新。

### 工程与文档

- **`start_services.py`**：启动流程提示等与本地联调衔接（与上文启用提示一致时可视为同一批改动）。
- **`开发计划.md`**：范围与进度更新。

## 0.1.16（2026-04-19）

### 本次说明

- 本次在后端新增**海康 NVR ISAPI 服务端代理与封装能力**：以已登记设备（`nvr_device`）为锚点发起到设备的 ISAPI 调用，提供常用能力封装、白名单网关、限流与可观测性补充；**生产环境凭据建议走环境变量或专用环境变量名映射，避免将真实密码写入仓库**。
- 保持与既有鉴权模型一致：**楼栋管理员**可使用封装接口；**超级管理员**可使用通用 ISAPI 网关（可按配置关闭）。

### 后端 · 海康 ISAPI（`backend/Aura.Api`）

- **新增端点组**：`Extensions/AuraEndpointsHikvisionIsapi.cs`，路由前缀 **`/api/device/hikvision`**（OpenAPI 标签「海康ISAPI」）。
  - 常用能力：`/device-info`、`/connectivity`、`/video-inputs/channels`、`/input-proxy/channels`、`/input-proxy/channels/status`、`/snapshot`、`/streaming/request-key-frame`、`/system/capabilities`、`/event/capabilities`、`/content-mgmt/zero-video-channels`、`/traffic/capabilities`、`/itc/capability`、`/sdt/picture-upload` 等。
  - 辅助能力：`GET /demo-catalog`（Demo 对照目录/说明，便于联调对照）、`POST /analyze-response`（解析设备 `ResponseStatus` 片段）。
  - 网关：`POST /gateway`（`PathAndQuery` 必须以 `/ISAPI/` 开头并受路径白名单约束；支持文本/二进制响应策略），默认仅**超级管理员**可用，且可由 `Hikvision:Isapi:GatewayEnabled` 关闭。
- **新增服务实现目录**：`Services/Hikvision/`（HTTP 客户端、选项与校验、路径白名单、网关执行、审计/截断策略、响应状态解析、指标与 Activity 等）。
- **请求模型**：`Models/Requests.cs` 增加海康相关 `record`（设备操作、抓图、网关、关键帧、SDT 图片上传、响应分析等），请求体可携带账号密码；为空时回落 `Hikvision:Isapi` 默认账号或**环境变量映射**（见 `ServiceExtensions` 中 `PostConfigure`）。
- **数据访问**：`Data/PgSqlStore.cs` 新增 `GetDeviceByIdAsync`，按 `device_id` 查询 `nvr_device`，供 ISAPI 调用解析设备 IP/端口等信息。

### 全局限流与请求体上限

- **`Program.cs`**：启用 `app.UseRateLimiter()`，与既有认证授权管道配合。
- **`ServiceExtensions.cs`**：注册 `AddRateLimiter`，策略 **`HikvisionGateway`** / **`HikvisionDeviceApi`**（按登录用户名或客户端 IP 做固定窗口；**`GatewayMaxRequestsPerMinute` / `DeviceApiMaxRequestsPerMinute` 为 0 时不限流**）；拒绝时返回 JSON：`code=42901`，`msg` 为中文“请求过于频繁，请稍后再试”。
- **`appsettings.json` / `appsettings.Testing.json`**：增加 **`Kestrel:Limits:MaxRequestBodySize`（10MB）**，与网关/上传等业务上限对齐，避免 Kestrel 默认限制与业务校验不一致。

### 可观测性

- **`OpenTelemetryExtensions.cs`**：Tracing 增加活动源 **`Aura.HikvisionIsapi`**（与 `Hikvision:Isapi:TelemetryActivitiesEnabled` 等开关配合）。
- **`Services/Hikvision/HikvisionIsapiMetrics.cs`** 等：补充 Prometheus 风格指标埋点（与现有 `/metrics` 体系一致）。

### 工程与集成测试

- **`Aura.Api.csproj`**：增加 **`InternalsVisibleTo`** 指向 `Aura.Api.Integration.Tests`，便于对内部类型做集成级测试。
- **`Aura.Api.Integration.Tests`**：新增 **`HikvisionIsapiLogFormattingTests`**、**`HikvisionIsapiOptionsValidatorTests`**、**`HikvisionIsapiPathGuardTests`**，覆盖日志格式化、选项校验与路径守卫等关键安全边界。

### 文档与第三方参考（本地/待纳入版本策略）

- **`docs/海康NVR-AppsDemo_ISAPI快速审查清单.md`**：AppsDemo 与 ISAPI 快速审查条目整理，便于与本后端封装对照联调。
- **`third-party/C#AppsDemo_ISAPI/`**（若纳入仓库）：海康官方 C# AppsDemo 与依赖树，作为接口与字段对照参考；体积较大，是否提交由团队仓库策略决定。

### 兼容性与质量说明

- 未改变既有抓拍/告警/资源树等核心业务路径；新增能力均为**独立路由组**，按需授权启用。
- 默认配置下网关审计日志可开启（测试环境可关闭相关审计/遥测开关以降低噪声），**请勿在配置文件写入生产设备明文密码**。

---

## 0.1.15（2026-04-18）

### 本次说明

- 本次为登录页视觉细节优化版本，重点提升品牌识别度与登录表单可读性，保持接口与业务逻辑不变。
- 调整遵循最小改动原则，仅修改登录页样式与品牌资源引用相关前端文件。

### 登录页（`frontend/login`）

- **品牌区布局优化**：
  - `frontend/login/login.css`：品牌区由竖排改为横排，图标与“寓瞳”同一行显示，图标在前，提升标题区紧凑度与识别效率。
- **品牌视觉强化**：
  - `frontend/login/login.css`：增大品牌图标尺寸；增大“寓瞳”字间距；移除品牌名称下方横线装饰，视觉更简洁。
- **表单间距优化**：
  - `frontend/login/login.css`：增大“用户名 / 密码 / 登录按钮”之间垂直间距，提升阅读节奏与点击前定位效率。

### 兼容性与质量说明

- 本次未新增第三方依赖，未改动接口路径与鉴权流程。
- 已保持浅色/暗黑主题变量体系不变，仅调整登录页局部布局与间距参数。

---

## 0.1.14（2026-04-18）

### 本次说明

- 本次为“楼层图纸 / 摄像头布点 / 重点防区”三页联动体验修复版本，重点解决“底图加载失败无感知或无兜底”“底部状态提示干扰画布操作”“楼层顺序与业务预期不一致”“部分提示含英文键名”等问题。
- 保持现有接口与数据结构不变，采用最小改动方式收敛前端展示与交互行为。

### 楼层图纸（`frontend/floor`）

- **预览失败自动回退占位图**：
  - `frontend/floor/floor.js`：楼层图预览加载失败时，自动回退占位图；候选优先复用已有楼层图资源，最终兜底内置占位图，避免新增依赖。
- **状态提示改为 Toast 优先**：
  - `frontend/floor/floor.js`：在支持 `window.aura.toast` 的环境下，页面状态提示改为 Toast 显示并隐藏底部提示区；无 Toast 能力时保留底部提示兼容。
- **提示文案中文化与格式优化**：
  - `frontend/floor/floor.js`：将 `floorId/nodeId` 英文键名提示统一为中文，并移除 `=`，如“楼层25，节点87”。

### 摄像头布点（`frontend/camera`）

- **底图失败自动回退占位图**：
  - `frontend/camera/camera.js`：楼层底图加载失败时自动回退占位图；补充空路径回退与加载序号保护，避免异步覆盖。
- **底部提示改为 Toast 显示**：
  - `frontend/camera/camera.js`：状态提示改为 Toast 优先，底部状态区在 Toast 模式下隐藏；并修复无 Toast 环境下提示递归调用隐患。
- **楼层切换顺序调整**：
  - `frontend/camera/camera.js`：楼层列表由升序改为降序（从大到小）。

### 重点防区编辑（`frontend/roi`）

- **底图失败自动回退占位图**：
  - `frontend/roi/roi.js`：保留并增强回退链路，支持空路径回退；优先复用已有楼层图资源，最终兜底内置占位图。
- **状态提示改为 Toast 优先**：
  - `frontend/roi/roi.js`：页面提示切换为 Toast 优先，并在 Toast 模式下隐藏底部提示区域，减少对画布操作干扰。
- **楼层切换顺序调整**：
  - `frontend/roi/roi.js`：楼层列表由升序改为降序（从大到小）。

### 兼容性与质量说明

- 本次未新增第三方依赖与外部静态资源引用，保持同源与现有资源策略。
- 已移除 `favicon` 作为占位候选，避免控制台出现无意义 `favicon.ico 404` 请求噪音。

---

## 0.1.13（2026-04-15）

### 本次说明

- 本次为“统计驾驶舱 + 三维空间态势”联动修复版本，重点解决“统计图表空白”“3D 与楼层标签选中不同步”“楼层堆叠方向不符合自然楼层认知”“2D 切片英文文案未本地化”等问题。
- 保持接口路径与权限策略不变，采用最小改动修复页面行为与展示一致性。

### 统计驾驶舱（`frontend/stats` + `backend/Aura.Api`）

- **后端统计取数修复**：
  - `backend/Aura.Api/StatsApplicationService.cs`：概览统计改为总量统计逻辑，图表统计改为按近 7 日时间范围聚合，避免默认 `limit=500` 截断导致数据偏差。
  - `backend/Aura.Api/Data/PgSqlStore.cs`：新增抓拍/告警总数统计与时间范围查询方法，支撑驾驶舱准确汇总。
- **图表空白根因修复**：
  - `frontend/stats/stats.css`：覆盖全局 `forms.css` 对 `.card` 的 flex 规则，恢复统计卡片块布局，并为 `.chart` 增加 `width: 100%`，修复图表容器宽度被压缩为 `0` 的问题。
- **前端诊断与容错增强**：
  - `frontend/stats/stats.js`：补充 ECharts 初始化/渲染错误提示、容器尺寸检测、渲染层检测、布局等待与重试机制，避免“有数据但图表不显示”时无定位信息。
- **状态提示体验优化**：
  - `frontend/stats/stats.js`：成功提示改为 Toast，底部状态栏仅保留错误信息，不再常驻成功文案。
  - `frontend/stats/stats.html`：增加 `favicon` 占位，消除控制台 `favicon.ico 404` 噪音。

### 三维空间态势（`frontend/scene`）

- **3D 点击与楼层标签联动修复**：
  - `frontend/scene/scene.js`：将楼层标签选中态刷新统一收敛到 `draw2DSlice`，修复“点击 3D 楼层后右上角楼层标签未同步选中”问题。
- **楼层堆叠顺序修复**：
  - `frontend/scene/scene.js`：建模前按 `floorId` 升序排序，确保 3D 场景中底部为 1 层、向上递增。
- **2D 切片文案本地化**：
  - `frontend/scene/scene.js`：画布左上角文案由英文 `Floor #x 2D Slice` 调整为中文 `第x层 2D 切片`。

### 安全策略补充（后端）

- `backend/Aura.Api/Program.cs`：默认 CSP `script-src` 增加 `'unsafe-eval'` 兼容项，确保本地 ECharts 运行时能力可用，避免策略拦截导致图表渲染失败。

---

## 0.1.12（2026-04-14）

### 本次说明

- 本次为多页面联动优化版本，重点覆盖楼层图纸、摄像头布点、重点防区、集宿资源树、三维态势与首页体验一致性。
- README 已包含根目录 `CHANGELOG.md` 的引用，本次新增并补齐该日志文件后，README 无需额外改动。

### 前端页面与交互优化

- 首页与通用外观
  - 统一多页面标题、信息层级与按钮语义风格，补齐暗黑/浅色主题下的视觉一致性。
  - 优化全局样式与公共壳层交互，增强页面可读性与状态反馈。

- 楼层图纸（`frontend/floor`）
  - 新增楼层列表侧栏与数量统计，支持按关键字筛选并快速切换楼层预览。
  - 上传与创建流程优化，增强空状态、错误状态与新窗口预览体验。

- 摄像头布点（`frontend/camera`）
  - 新增楼层切换列表及每层点位数量展示，支持按楼层快速切换画布。
  - 新增点位采用“先进入新增模式再点击画布”的流程，减少误操作。
  - 补充点位弹窗录入与提示逻辑，增强保存与刷新后的状态反馈。

- 重点防区（`frontend/roi`）
  - 页面重构为“左侧参数/楼层切换 + 右侧底图与防区”布局。
  - 新增楼层数量与楼层切换能力，支持按楼层自动回显防区与底图。
  - 新增防区改为弹窗配置（摄像头ID、房间节点ID）后进入标注流程。
  - 调整操作区位置与文案：按钮集中到“底图与防区”右上方，刷新按钮文案统一为“刷新”。
  - 新增防区保存流程优化：在标注状态下可直接触发保存并自动刷新结果。

- 集宿资源树（`frontend/campus`）
  - 增强页头信息、统计徽标、搜索过滤、全部展开/全部收起能力。
  - 资源树展示重构为“园区-楼栋-楼层-房间”向右分级结构，强化层级关系。
  - 修复全部展开/收起在部分环境下无响应的问题，提升选择器兼容性。
  - 修复折叠图标在字体环境下显示方块的问题，改为纯 CSS 箭头绘制。

- 其他页面联动调整
  - `frontend/scene`：优化三维态势说明、事件流展示与样式细节。
  - `frontend/index`、`frontend/log`、`frontend/judge`、`frontend/track` 等页面同步做样式与结构对齐，提升全站体验一致性。

### 后端与配置补充

- `backend/Aura.Api` 部分端点与数据存储逻辑完成同步调整，支撑前端联动改造后的查询与展示需求。
- 开发环境配置（`appsettings.Development.json`）做了与当前联调流程匹配的更新。

### 脚本与联调支持

- 新增 `scripts/seed_smoke_data.py`，用于一键注入冒烟数据（资源树、楼层、摄像头、防区、抓拍、告警、研判样例等），便于本地验收与演示回归。

### 兼容性与质量说明

- 重点交互均保持原接口路径与权限模型不变，以最小行为变更完成体验增强。
- 前端新增交互以中文提示为主，便于运维和业务同学快速理解状态。

---

## [0.1.11] - 2026-04-14

### 前端 · 页面与全局交互统一升级

- **全局样式与交互底座**：`frontend/common/forms.css`、`frontend/common/shell.css`、`frontend/common/theme.css`、`frontend/common/shell.js` 大幅增强，统一了按钮语义（`btn-primary/btn-secondary/btn-danger`）、Toast、分页器（`aura-pager`）、弹窗基础交互、顶栏主题切换与状态提示。
- **按钮可见性优化（浅色/深色）**：次要按钮与危险按钮全部纳入主题变量控制，强化边框/背景/阴影与 hover 反馈；修复在深浅主题下“按钮存在感弱、操作不显著”的问题。
- **用户管理重构**：`frontend/user/user.html/.css/.js` 增加创建/重置密码/删除弹窗流程、列表分页、关键词过滤、创建时间与最后登录时间展示、CSV 模板下载、批量导入、导出能力，并统一复用全局按钮/表格/弹窗样式。
- **角色管理增强**：`frontend/role/role.html/.css/.js` 优化角色列表渲染、权限中文展示、分页能力与创建弹窗流程；移除“查询角色”按钮的页面私有样式覆盖，回归全局按钮体系。
- **跨页面一致性收敛**：`alert/camera/campus/capture/device/floor/index/judge/log/login/roi/scene/search/stats/track` 等页面的 `html/js/css` 同步接入全局样式与壳层交互能力，减少页面私有重复实现，提升一致性。

### 后端 · 时间序列化、用户域与日志查询能力

- **时间序列化统一**：新增 `backend/Aura.Api/Serialization/AuraJsonSerializerOptions.cs` 与 `DateTimeDisplayJsonConverters.cs`，将 `DateTime/DateTimeOffset` 统一序列化为 `yyyy-MM-dd HH:mm:ss` 展示格式，降低前端解析与显示分歧。
- **系统日志查询服务化**：新增 `backend/Aura.Api/SystemLogQueryService.cs`，并在 `AuraEndpointsDomain` 中提供系统日志列表查询入口（分页 + 关键词过滤 + 内存回退）。
- **用户域能力补齐**：`IdentityAdminService`、`PgSqlStore`、`Models/Entities.cs`、`Models/Requests.cs` 等同步支持用户 `display_name`、`last_login_at`、展示昵称与登录时间链路；登录/用户查询流程与实体映射保持一致。
- **端点与服务扩展**：`AuraEndpointsAuth/Core/Domain`、`ServiceExtensions`、`Program`、`AppStore`、`RetryQueueService`、`CaptureProcessingService`、`RetryProcessingService`、`DeviceManagementService`、`JudgeService` 等完成一轮协同调整，统一时间类型与接口返回结构，完善服务注入与运行稳定性。

### 后端与数据库 · 研判结果时间类型一致性修复

- **Dapper 物化修复**：`backend/Aura.Api/Data/PgSqlStore.cs` 中 `DbJudgeResult` 改为显式属性 + 构造函数映射，修复 `GetJudgeResultsAsync` 物化失败（构造签名不匹配）问题。
- **CreatedAt 统一语义**：`DbJudgeResult` 对外统一 `CreatedAt: DateTimeOffset`，并兼容 `DateTime`/`DateTimeOffset` 双构造入参，避免驱动或字段类型差异导致的时间映射异常。
- **开发库基线优化**：`database/schema.pgsql.sql` 将 `judge_result.created_at` 基线改为 `TIMESTAMPTZ`；未部署开发环境可直接按基线建库，无需增量迁移脚本。

### 脚本、部署与测试

- **启动脚本增强**：`start_services.py` 完善开发预检与就绪检查流程（连接串占位检测、端口占用清理、AI/.NET JSON 探针、管理员自动登录 + readiness 校验），提升本机全栈联调稳定性。
- **部署脚本收敛**：`docker/deploy-aura-ubuntu.sh` 对齐 .NET 10.0.201 镜像版本、补齐生产环境变量与提示文案，强化命名卷保留与删卷风险说明。
- **集成测试补充**：`backend/Aura.Api.Integration.Tests/HealthEndpointTests.cs` 同步更新健康检查相关断言与用例，覆盖本轮核心健康路径改动。
- **存储目录占位**：`storage/.gitkeep` 纳入版本管理，确保开发与部署环境在仓库层具备稳定目录基线。

### 0.1.11 补充修订（导出链路与数据表统一）

- **导出能力全局统一**：`frontend/common/shell.js` 新增全局导出方法 `window.aura.exportDataset(options)`，统一处理“选择格式 -> 请求导出接口 -> 解析 `downloadUrl` -> 打开下载链接”流程。
- **业务页导出改造**：`frontend/capture/capture.js`、`frontend/alert/alert.js`、`frontend/judge/judge.js`、`frontend/log/log.js`、`frontend/user/user.js` 全部改为复用全局导出方法，并为导出点击统一加入 `preventDefault/stopPropagation` 防止误刷新。
- **后端导出数据集扩展与兼容**：`backend/Aura.Api/Export/ExportApplicationService.cs` 新增 `dataset=user` 导出，支持用户名/昵称关键字过滤；同时增加 `log/logs/systemlog/users/userlist` 等历史别名兼容映射，降低前后端版本错配风险。
- **日志页导出交互修复**：`frontend/log/log.js` 与 `frontend/log/log.html` 调整为“有数据才显示导出按钮、无数据隐藏”，并与后端 JSON 导出返回协议对齐（不再直接打开导出接口 JSON 页面）。
- **用户/角色数据表样式收敛**：`frontend/user/user.css`、`frontend/role/role.css` 移除页面私有表格视觉重写，改为完全复用 `frontend/common/forms.css` 的全局 `.aura-data-table` 规范。
- **操作列全局规范补齐**：`frontend/common/forms.css` 新增 `aura-col-action-group` 与 `aura-table-actions` 语义类，并统一收紧操作列按钮尺寸与单元格上下内边距，修复“按钮显示不完整、行高被撑高”问题。

### 按文件落点（审计清单）

- **后端核心**：`backend/Aura.Api/Program.cs`、`backend/Aura.Api/Extensions/AuraEndpointsAuth.cs`、`backend/Aura.Api/Extensions/AuraEndpointsCore.cs`、`backend/Aura.Api/Extensions/AuraEndpointsDomain.cs`、`backend/Aura.Api/Extensions/ServiceExtensions.cs`、`backend/Aura.Api/Data/PgSqlStore.cs`、`backend/Aura.Api/Data/AppStore.cs`、`backend/Aura.Api/Models/Entities.cs`、`backend/Aura.Api/Models/Requests.cs`、`backend/Aura.Api/Internal/DevInitializer.cs`、`backend/Aura.Api/IdentityAdminService.cs`、`backend/Aura.Api/DeviceManagementService.cs`、`backend/Aura.Api/JudgeService.cs`、`backend/Aura.Api/Capture/CaptureProcessingService.cs`、`backend/Aura.Api/RetryProcessingService.cs`、`backend/Aura.Api/Cache/RetryQueueService.cs`。
- **后端新增文件**：`backend/Aura.Api/Serialization/AuraJsonSerializerOptions.cs`、`backend/Aura.Api/Serialization/DateTimeDisplayJsonConverters.cs`、`backend/Aura.Api/SystemLogQueryService.cs`。
- **数据库与部署脚本**：`database/schema.pgsql.sql`、`docker/deploy-aura-ubuntu.sh`、`start_services.py`、`storage/.gitkeep`。
- **前端全局公共层**：`frontend/common/forms.css`、`frontend/common/shell.css`、`frontend/common/shell.js`、`frontend/common/theme.css`。
- **前端业务页面（HTML/CSS/JS）**：`frontend/alert/*`、`frontend/camera/*`、`frontend/campus/*`、`frontend/capture/*`、`frontend/device/*`、`frontend/floor/*`、`frontend/index/*`、`frontend/judge/*`、`frontend/log/*`、`frontend/login/*`、`frontend/roi/*`、`frontend/role/*`、`frontend/scene/*`、`frontend/search/*`、`frontend/stats/*`、`frontend/track/*`、`frontend/user/*`。
- **测试与文档**：`backend/Aura.Api.Integration.Tests/HealthEndpointTests.cs`、`CHANGELOG.md`。

---

## [0.1.10] - 2026-04-13

### 后端 · 统一存储路径与楼层图上传修复

- **`Internal/ProjectPaths.cs`**：新增仓库根与 **`storage`** 的**唯一解析入口**——优先从 **`ContentRoot` 向上查找 `Aura.sln`** 定位仓库根，失败则回退为「**`ContentRoot` 的上上级**」（兼容本地 **`backend/Aura.Api`** 与容器 **`/app`**）；**`ResolveStorageRoot`** 固定为 **`{仓库根}/storage`**；**`ResolvePathRelativeToProjectRoot`** 将配置中的相对路径解析为**相对仓库根的绝对路径**，**不依赖进程当前工作目录**。
- **`Program.cs`**、**`ServiceExtensions.cs`**、**`Middleware/FrontendMiddleware.cs`**、**`AuraEndpointsCampusFloor.cs`**：静态 **`/storage`** 与各服务注入的 **`storageRoot`** 均改为使用 **`ProjectPaths`**，与「仅使用仓库根下 **`storage/`**」的设计一致。
- **楼层图上传**：**`POST /api/floor/upload`** 落盘目录与 **`Program`** 中 **`UseStaticFiles(/storage)`** 的物理根一致，修复此前用 **`AppContext.BaseDirectory`** 推算导致文件写入 **`backend` 侧错误目录**、预览 **`/storage/...` 返回 404** 的问题。
- **告警落盘**：**`Ops:Alert:FilePath`** 在注册 **`AlertNotifier`** 时经 **`ResolvePathRelativeToProjectRoot`** 解析；**`AlertNotifier`** 对**非绝对路径**拒绝写入并打日志，避免 **`Path.GetFullPath` 相对 CWD**（如 **`start_services`** 将 API 工作目录设为 **`backend/Aura.Api`**）在 **`backend/Aura.Api/storage`** 下误建目录。

### 仓库

- **`.gitignore`**：增加 **`backend/**/storage/`**，避免误将 **`backend` 下误生成的 `storage`** 提交入库。

---

## [0.1.9] - 2026-04-13

### 前端 · 三维空间态势页

- **`frontend/scene/scene.css`**：右侧栏收紧纵向间距，并为 **`.right`** 设置 **`min-height: 0`** 与 **`overflow: hidden`**，与主区域 flex 布局配合，减轻整栏内容外溢。
- **统计区**：桌面端由 **2×2** 调整为 **一行四列**（窄屏 **≤900px** 回退两列、**≤720px** 单列）；卡片内 **标签与数值横向排列**，减小内边距与字号，降低统计区高度。
- **「实时事件流」**：对应右栏第 4 块面板使用 **`flex: 1`** 占据剩余高度，**`.event-feed`** 取消固定 **`max-height: 240px`**，仅在列表区域内部滚动，尽量避免 **浏览器整页纵向滚动条**。
- **2D 楼层切片**：**`#slice2d`** 增加 **`max-height: min(220px, 28vh)`** 与 **`display: block`**，控制画布占高。
- **楼层态势**：**`.floor-summary`** 降低 **`min-height`**、收紧内边距与字号；**`.panel`** / **`.panel-title-row`** 间距略减。
- **底部状态 `#result`**：改为独立状态条样式（边框、主题变量背景、**`pre-wrap`**、合理 **`min-height` / `max-height`** 与 **`overflow-y: auto`**），避免过小的 **`max-height` + `overflow: hidden`** 导致文案 **裁切或观感压扁**；使用列方向 **`flex`** 与 **`justify-content: safe center`**（并保留 **`center`** 回退），使 **「操作完成」** 等 **单行提示在框内垂直居中**，内容过高时仍以顶部为安全对齐并可在区域内滚动。

---

## [0.1.8] - 2026-04-13

### 可观测性

- **OpenTelemetry**：引入 **`OpenTelemetry.Extensions.Hosting`**、**`Instrumentation.AspNetCore`**、**`Instrumentation.Http`**、**`Exporter.OpenTelemetryProtocol`**；通过 **`Ops:Telemetry:EnableTracing`** 与 **`Ops:Telemetry:OtlpEndpoint`**（或 **`OTEL_EXPORTER_OTLP_ENDPOINT`**）按需启用 OTLP 导出；ASP.NET 采集过滤 **`/metrics`** 以降低噪声。

### AI 与网关

- **AI API Key**：FastAPI 在设置环境变量 **`AURA_API_KEY`** 时校验请求头 **`X-Aura-Ai-Key`**（根路径 **`/`** 与 OpenAPI 文档路径除外）；.NET 配置 **`Ai:ApiKey`** 后由 **`HttpClient`** 默认附加同名请求头。
- **Compose / 模板**：**`docker-compose.full.example.yml`** 与 **`docker/.env.full.example`** 增加可选 **`AURA_API_KEY`** / **`Ai__ApiKey`**；根目录 **`.env.example`** 与真实 **`.env`** 键名对齐（本机联调：脚本登录、**.NET**、**AI**、可观测性、Arango 等），并注明与 **`docker/.env*.example`** 分工。
- **`start_services.py`**：开发预检中 **`Jwt__Key`** 支持从环境变量（根目录 **`.env`**）覆盖，与 **`.env.example`** 约定一致。

### 前端与 CI

- **ESLint**：**`frontend/package.json`**、**`eslint.config.cjs`**（忽略 **`common/vendor/**`**），修正少量 **`no-unused-vars`** 与全局 **`THREE` / `echarts`** 声明；**`dotnet-ci.yml`** 增加 Node 20、**`npm ci`** 与 **`npm run lint`**。
- **Dependabot**：增加 **`npm`** 生态 **`/frontend`**。

### Kubernetes

- **`deploy/k8s/`**：**`README.md`**（策略说明）、**`ingress-nginx-deny-public-metrics.example.yaml`**（公网 Ingress 拒绝 **`/metrics`**）、**`network-policy-api.example.yaml`**（入站基线示例）。

---

## [0.1.7] - 2026-04-13

### 可观测性与安全基线

- **Prometheus**：接入 **`prometheus-net.AspNetCore`**，在管道中启用 **`UseHttpMetrics`**，并按配置 **`Ops:Metrics:ExposePrometheus`** 映射 **`GET /metrics`**（默认开启；**`appsettings.Testing.json`** 中为 **`false`** 以免测试环境暴露抓取端点）。
- **前端路由中间件**：将 **`/metrics`** 视为保留路径，避免未登录访问被重定向到登录页而无法抓取指标。
- **容器镜像**：**`docker/backend.Dockerfile`**、**`docker/ai.Dockerfile`** 增加非 **`root`** 运行用户 **`aura`**，发布产物与 **`/app`** 目录按属主调整；**`docker/README.md`**「安全建议」补充卷权限与非 root 说明。

### 仓库与配置

- **`.env.example`**：根目录环境变量模板（双下划线配置键、脚本账号变量），与 **`docker/.env*.example`** 分工说明写在文件头注释中。
- **`.gitignore`**：增加 **`!.env.example`**，确保该模板可被提交与克隆后可见。
- **生产配置模板**：**`appsettings.Production.json`** 补充 **`Ops:Metrics`** 段，与基线 **`appsettings.json`** 对齐。

### 文档

- **`README.md`**：版本 **`0.1.7`**，补充 **`/metrics`** 与 **`Ops:Metrics:ExposePrometheus`** 说明。

---

## [0.1.6] - 2026-04-13

### 运维脚本与文档

- **`start_services.py`**：就绪探测改为 **`_wait_http_json_probe`**，仅接受 **HTTP 2xx**，并对 **AI**（`code=0` 且 **`model_loaded=true`**）与 **.NET**（`/api/health` 的 `code=0` 且 `msg` 含「寓瞳」）做 JSON 校验，避免 404 等被误判为已就绪；文件头补充与 **Testing** 环境的适用边界说明。
- **`start_services.py`**：更正 **`_extract_dev_admin_password_from_log_line`** 文档字符串，与 **`DevInitializer`** 当前固定 **`123456`** 及日志格式一致。
- **`README.md`**：在「本机一键启动与就绪检查」中补充探针语义、全栈前置条件及与 **`readiness`** 的衔接说明。

---

## [0.1.5] - 2026-04-13

### 后端：企业级韧性、可观测与错误边界

- **出站 HTTP 弹性**：引入 `Microsoft.Extensions.Http.Resilience`，为 **AI 服务**（`AiService`）与 **告警 Webhook**（`AlertNotifier`）命名 `HttpClient` 配置标准重试/超时/熔断；`HttpClient.Timeout` 设为无限，由管道控制总时长。超时与重试次数可通过 **`HttpClients:Ai`**、**`HttpClients:AlertNotifier`** 配置（见 `appsettings.json`）；熔断采样窗口按尝试超时自动放大以满足框架校验。
- **全局异常处理**：非 `Development` 环境使用统一 JSON 响应（`code: 50000`、中文 `msg`、**`traceId`**），不向客户端返回堆栈；开发环境启用 **`UseDeveloperExceptionPage`**。
- **请求关联 ID**：新增 **`CorrelationIdMiddleware`**，支持请求头 **`X-Correlation-Id`** 透传或自动生成，写入响应头与日志作用域；**`PureConsoleFormatter`** 在日志行前输出 `[关联Id]`。
- **存活探针**：新增 **`GET /api/health/live`**（无鉴权、无外部依赖，返回 `{ "status": "alive" }`），供负载均衡/K8s liveness；原 **`GET /api/health`** 保留业务向提示。
- **生产主机头**：**`appsettings.Production.json`** 中 **`AllowedHosts`** 由 `*` 改为占位域名，上线前需替换为真实主机名；根 `appsettings.json` 保留注释说明。
- **启动日志**：生命周期日志中的环境名称改为输出 **`EnvironmentName`**（如 `Testing`、`Production`），避免非 Development 被误标为「生产环境」。

### CI 与测试

- **漏洞扫描**：`dotnet-ci.yml` 增加 **`dotnet list package --vulnerable --include-transitive`**。
- **集成测试**：补充 **`/api/health/live`**、响应头 **`X-Correlation-Id`** 及透传一致性用例。

### 文档

- **`README.md`**：版本 `0.1.5`，关键接口与集成测试小节补充探针与关联 ID、`AllowedHosts` 说明。

---

## [0.1.4] - 2026-04-13

### 后端：路由拆分、就绪探测、限流与 HttpClient

- **路由模块化**：将 `MapAuraEndpoints` 拆为 `AuraEndpointsCore` / `Auth` / `CampusFloor` / `DeviceCapture` / `Domain` 多文件，入口仍集中在 `Extensions/EndpointExtensions.cs`。
- **PostgreSQL 就绪检查**：`/api/ops/readiness` 的 `pgsql` 项改为执行 `SELECT 1` 真实探测，不再恒为 `true`。
- **登录限流**：`/api/auth/login` 在 Redis 可用时按「客户端 IP + 用户名」维度限流（每分钟 20 次），降低暴力尝试风险；未启用 Redis 时与其它限流一致不拦截。
- **告警 HttpClient**：`AlertNotifier` 改为通过 `IHttpClientFactory` 命名客户端 `AlertNotifier` 创建，避免裸 `new HttpClient()` 的长连接问题。
- **内存回退开关**：新增配置 `Aura:AllowInMemoryDataFallback`（默认 `false`；开发环境 `appsettings.Development.json` 为 `true`）。为 `false` 时，列表类接口在数据库无行时返回空集合，写入失败返回 503，不再静默写入内存 `AppStore`。
- **SignalR 提示**：在 `AddSignalR` 处增加中文注释，提醒多实例需 Redis Backplane。
- **集成测试**：新增 `backend/Aura.Api.Integration.Tests`（xUnit + `WebApplicationFactory`），覆盖 `GET /api/health`；根目录增加 `Program.Public.cs` 中的 `public partial class Program` 供工厂引用。
- **测试环境**：`appsettings.Testing.json` + `AuraApiFactory`（`ASPNETCORE_ENVIRONMENT=Testing`）避免连接本机 Redis/PG、跳过开发库初始化，并补充未登录根路径重定向用例。
- **集成测试补充**：`TestingJwt` 与 Testing 配置对齐签发 Cookie 用 JWT，覆盖「已登录访问 `/` → `/index/`」前端路由中间件行为（无需真实数据库登录）。
- **文档提示**：`README.md` 目录说明与「集成测试（维护者）」小节、`appsettings.Testing.json` 与 `TestingJwt.cs` 文件头均注明：修改 Testing 环境 JWT 配置须同步更新测试常量。
- **CI**：新增 `.github/workflows/dotnet-ci.yml`，在推送/PR 时执行 `dotnet build`、自检工程 `dotnet run` 与集成测试。

### 前端与 AI

- **主题与态势页**：在 `common/theme.css` 补充场景用色板变量；`scene/scene.css` 改为引用主题变量与 `color-mix`，减少页面内硬编码色值。
- **Python 依赖锁定**：`ai/requirements.txt` 改为固定版本号，便于复现构建。

### 数据库

- **迁移目录**：新增 `database/migrations/README.txt`，约定增量 SQL 命名与执行顺序（基线仍以 `schema.pgsql.sql` 为准）。

---

## [0.1.3] - 2026-04-11

### 后端：依赖注入架构加固与启动稳定性修复

- **依赖注入（DI）修正**：完全解决了因在根服务容器（Root Provider）中解析 Scoped 服务（如 `JudgeService`、`EventDispatchService`）导致的启动崩溃。所有 Scoped 服务均改为在 Minimal API 路由处理程序中直接注入，或在后台任务回调中使用 `app.Services.CreateScope()` 手动创建作用域解析。
- **端点映射稳定性**：修复了 `EndpointExtensions.cs` 中的语法错误和变量引用冲突（如 `captureGroup` 变量丢失、异步 lambda 返回值类型不明确、`request` 变量名误用等）。
- **Dapper 映射修复**：修正了 `PgSqlStore` 中 `DbCapture` 的物化失败问题。通过在 SQL 查询中显式投影 `image_path` 字段，使其与 record 构造函数签名完全对齐。
- **数据库一致性**：将 API 调用中的 `GetTrackEventsByVidAsync` 统一回退为 `PgSqlStore` 实际定义的 `GetTrackEventsAsync`。

### 运维与日志：全中文纯净日志体系

- **日志汉化与去噪**：
  - **自定义格式化器**：实现 `PureConsoleFormatter`，彻底移除了控制台日志中的 `info: Program[0]` 等技术性类名前缀，仅显示纯净业务消息。
  - **屏蔽框架英文日志**：通过 `appsettings.json` 屏蔽了 Microsoft 托管生命周期（`Hosting.Lifetime`）和 `HttpClient` 的默认英文追踪日志。
  - **全局文化区域**：在 `Program.cs` 中强制设置 `zh-CN` 文化区域，并在 `EndpointExtensions.cs` 中将环境标识汉化。
  - **生命周期汉化**：通过 `app.Lifetime` 钩子手动实现了全中文的启动状态、监听地址及运行环境提示。
  - **推理服务汉化**：同步汉化了 AI 推理服务（Python/ONNX）的初始化日志与特征提取错误提示。
  - **脚本输出人性化**：优化了 `start_services.py` 的就绪检查输出，将原始 JSON 字典转换为友好的中文清单（如“JWT 密钥: 已就绪”）。
- **环境信任**：自动信任 ASP.NET Core 开发证书，消除了 Kestrel 启动警告。

---

## [0.1.2] - 2026-04-11

### 后端：`Program.cs` 模块化与扩展点收敛

- **`backend/Aura.Api/Program.cs`**：由「单文件承载绝大部分路由与 DI」改为精简启动入口；服务注册迁至 **`Extensions/ServiceExtensions.cs`**（`AddAuraServices`），路由映射迁至 **`Extensions/EndpointExtensions.cs`**（`MapAuraEndpoints`）。
- **中间件**：安全响应头 **`Middleware/SecurityHeadersMiddleware.cs`**；**`Program.cs`** 使用 **`Middleware/FrontendRoutingMiddleware.cs`** 处理无扩展名路径与登录重定向；另提供 **`Middleware/FrontendMiddleware.cs`** 的 **`UseAuraFrontend`** 扩展（当前启动链未调用，可按需接入以集中 CSP 与静态根配置）。
- **开发初始化**：**`Internal/DevInitializer.cs`** 承担 Development 下管理员种子/密码重置逻辑；日志通过 **`ILoggerFactory.CreateLogger`** 创建，避免非法泛型 `ILogger<typeof(...)>`。
- **通用辅助**：**`Internal/AuraHelpers.cs`** 承载抓拍校验、限流、HMAC、操作日志等横切逻辑；抓拍校验入参统一为 **`CapturePayload`**；限流维度使用 **`ClaimsPrincipal.FindFirst`**，消除对 **`FindFirstValue`** 扩展方法的依赖。

### 业务服务文件化（原内联逻辑落地为独立类型）

- 新增/收敛：`IdentityAdminService`、`DeviceManagementService`、`JudgeService`、`ResourceManagementService`、`MonitoringQueryService`、`CaptureProcessingService`、`CaptureOpsService`、`RetryProcessingService`、`OutputApplicationService`、`StatsApplicationService`、`SpaceCollisionService`、`VectorApplicationService` 等（位于 `backend/Aura.Api` 各目录）。
- **聚类**：**`Clustering/ClusterApplicationService.cs`**、**`Clustering/FeatureClusteringService.cs`**。
- **导出**：**`Export/ExportApplicationService.cs`**、**`Export/TabularExportService.cs`**（CSV/XLSX）。
- **SignalR**：**`Hubs/EventHub.cs`** 统一为角色组订阅入口（连接/断开时维护 `role:*` 分组）；**`Ops/EventDispatchService.cs`** 通过 **`IHubContext<EventHub>`** 推送；移除重复的 **`Ops/EventHub.cs`**，避免 Hub 类型冲突。

### 模型与内存存储去重

- 删除与 **`Requests.cs` / `Entities.cs` / `ViewModels.cs`** 重复的 **`Models/AuraModels.cs`**（保留 **`Services/DailyJudgeHostedService.cs`** 内的 **`DailyJudgeScheduleState`** 为唯一定义）。
- 删除重复的 **`Models/AppStore.cs`**，统一使用 **`Data/AppStore.cs`**（`List<>` 语义）；**`IdentityAdminService` / `DeviceManagementService`** 的内存兜底路径由 **`ConcurrentDictionary`** 风格改为 **`List` + `FindIndex`** 等，与 **`ResourceManagementService`** 等资源类一致。

### 依赖注入（DI）与配置路径

- **`AddAuraServices`** 增加 **`IHostEnvironment`** 参数，与 **`Program`** 一致解析 **`storage` 根目录**（`ContentRoot` 上溯一级 + `storage`），用于 **`ExportApplicationService`**、**`ResourceManagementService`**、**`CaptureProcessingService`** 等需磁盘路径的服务。
- 注册 **`FeatureClusteringService`**、**`TabularExportService`**（Singleton）；**`VectorApplicationService`** 的 **`int`** 上限来自配置 **`Limits:MaxImageBase64Chars` / `Limits:MaxMetadataJsonChars`**（缺省与接口侧大页一致：5_000_000 / 200_000）。
- **`CaptureProcessingService`**：重试图片目录由 **`Storage:CaptureRetryRoot`** 解析（空则 **`{storage}/captures/retry`**），布尔项读取 **`CaptureRetry:*`**、**`Storage:SaveCaptureImageOnSuccess`**。
- **`Program.cs`**：调用 **`AddAuraServices(builder.Configuration, builder.Environment, isDev)`**。

### 路由与数据访问对齐

- **`EndpointExtensions`**：**`/api/campus/update`** 调用 **`PgSqlStore.UpdateCampusNodeAsync`**（替代不存在的 **`UpdateCampusNodeNameAsync`**）；轨迹/研判列表分别使用 **`GetTrackEventsAsync`**、**`GetJudgeResultsAsync`**；**`/api/cluster/list`** 注入 **`MonitoringQueryService`**；就绪检查 **`alertNotify`** 分支消除 **CS8629**（显式抽取 **`LastFailureAt`** 与时间窗口变量）。
- **`PgSqlStore`**：**`DbCapture`** 增加可选 **`ImagePath`** 字段，与聚类/抓拍查询中对 **`ImagePath`** 的投影一致。

### 编码与文案修复

- **`Export/ExportApplicationService.cs`**：修复因编码损坏导致的字符串字面量断裂；导出表头、错误提示、操作日志与 **`ExportDatasetTitleCn`** 恢复为可读简体中文。
- **`Middleware/FrontendMiddleware.cs`**：文件头注释乱码修正为「前端路由与安全响应头中间件」说明。
- **`Clustering/ClusterApplicationService.cs`**：操作日志操作者/动作由乱码改为 **「系统任务」/「聚类执行」**。

### 认证与代码质量

- **`IdentityAdminService`**：角色归一化复用 **`AuraHelpers.ConvertRole`**，删除未穷尽的私有 **`ConvertRole`**，消除 **CS0161**。

### 工程与解决方案

- 新增 **`Aura.sln`**、根级 **`Directory.Build.props`**（将部分工程的中间输出引导至 **`.verify_build\obj`**，并排除误编译 **`obj`** 下生成文件）、**`global.json`**（SDK 版本约束）。
- 新增轻量 **`backend/Aura.Api.Tests`** 工程（聚类/导出等纯逻辑自检入口，可按需扩展）。

### 后端：定时任务与 Scoped 生命周期

- **`backend/Aura.Api/Program.cs`**：归寝定时研判委托（**`DailyJudgeScheduleState.RunDailyAsync`**）内通过 **`IServiceScope`**（**`app.Services.CreateScope()`**）解析 **`JudgeService`**，避免从根 **`IServiceProvider`** 解析 Scoped 服务触发 **`InvalidOperationException`**。

### Docker：镜像与 `global.json` 对齐、持久化与脚本

- **`docker/backend.Dockerfile`**：默认 **`DOTNET_SDK_IMAGE` / `DOTNET_ASPNET_IMAGE`** 由 **`10.0-preview`** 调整为 **`10.0.201`**，与根目录 **`global.json`** 中 **`sdk.version`** 一致；注释说明升级 SDK 时需同步维护。
- **`docker/.env.full.example`**、**`docker/.env.prod.example`**、**`docker/deploy-aura-ubuntu.sh`**：同上对齐 **`10.0.201`**；**`.env.full.example`** 补充命名卷 **`aura-api-storage`** 与 **`docker compose down`** 默认保留卷的说明。
- **`docker/docker-compose.full.example.yml`**：为 **`api`** 增加命名卷 **`aura-api-storage` → `/app/storage`**，持久化抓拍、导出、告警落盘等数据。
- **`docker/docker-compose.prod.template.yml`**：为 **`api`** 增加 **`aura-api-storage:/app/storage`**；可选环境变量 **`Paths__FrontendRoot: ${PATHS__FRONTENDROOT:-}`**；**`docker/.env.prod.example`** 补充 **`PATHS__FRONTENDROOT`** 可选配置说明注释。
- **`.github/workflows/docker-build-push.example.yml`**：为 **`DOTNET_*`** Secret 增加与 **`global.json`** 对齐的注释提示。
- **`docker/README.md`**：新增「镜像版本与仓库 SDK 对齐」「持久化策略（storage）」；**`down`/`down-full` 默认保留命名卷**及 **`down-full -Volumes` / `down-full.sh --volumes`** 删卷说明；开篇明确仓库根目录 **`.env.example`** 与 **`docker/.env.full.example`** 分工；「上线就绪巡检」步骤与根目录 **`.env.example`** 说明一致；合并精简「生产模板说明」；**`deploy-aura-ubuntu.sh`** 列入目录索引。
- **`docker/up-full.ps1`**、**`docker/up-full.sh`**：启动成功后提示命名卷在普通 **`down`** 时默认保留。
- **`docker/down-full.ps1`**：支持 **`-Volumes`**，等价 **`docker compose down -v`**（慎用，会删除数据库等卷）。
- **`docker/down-full.sh`**：支持 **`-v` / `--volumes`**，行为同上。
- **`docker/deploy-aura-ubuntu.sh`**：部署结束输出中增加命名卷与 **`down -v`** 风险说明。

### `.dockerignore` 与构建上下文

- **`.dockerignore`**：增加 **`.verify_build`**、**`backend/Aura.Api.Tests`**、**`docs`**，缩小镜像构建上下文并排除无关目录。

## [0.1.1] - 2026-03-25

### 本机直跑配置收敛与就绪检查自动化

- `start_services.py`：启动前自动读取根目录 `e:\Aura\.env` 注入环境变量，启动预检优先使用 `.env` 提供的 `ConnectionStrings__PgSql`、`ConnectionStrings__Redis` 与 `Ai__BaseUrl`，避免 appsettings 中的占位配置误用。
- `start_services.py`：启动前自动清理占用 `8000`（AI）与 `5001`（后端）端口的进程，避免重复启动导致 `address already in use` 与构建文件锁定问题。
- `start_services.py`：启动后自动使用“超级管理员”账号登录，并调用 `GET /api/ops/readiness` 完成就绪检查；打印 `[readiness] ready=... , checks=...`。支持 `--run-until-ready / --check-only` 模式，便于本机联调与 CI 预检。
- `.env.example`：补齐 `ConnectionStrings__PgSql`、`ConnectionStrings__Redis`、`ARANGO_*`、`Jwt__Key`、`Security__HmacSecret`、`Ai__BaseUrl` 等模板键，降低多处配置不一致的风险。

### AI 与配置严格化

- `ai/main.py`：Arango 连接不再使用测试默认值，必须通过 `ARANGO_USER / ARANGO_PASSWORD` 明确配置；健康接口返回 `arango_error`，避免静默降级导致“检索未落库”难排查问题。
- `backend/Aura.Api/appsettings.Development.json`：`ConnectionStrings:PgSql/Redis` 改为占位值，确保本机直跑以 `.env` 为唯一数据源。

### 开发环境账号便利化

- `backend/Aura.Api/Program.cs`：Development 下 `admin` 密码固定为 `123456`，配合 readiness 自动化减少联调摩擦（生产环境仍需关闭开发自动化能力并替换密钥）。

## [0.1.0] - 2026-03-25

### 架构重构：旧关系库迁移至 PostgreSQL，保留 ArangoDB

- `backend/Aura.Api/Aura.Api.csproj`：数据访问驱动已切换为 `Npgsql`。
- `backend/Aura.Api/Data/PgSqlStore`：核心存储实现类升级为 `PgSqlStore`，SQL 方言同步切换 PostgreSQL（`RETURNING`、`LIMIT/OFFSET`、`JSONB`）。
- `backend/Aura.Api/Program.cs`：连接配置统一改为 `ConnectionStrings:PgSql`，就绪检查项改为 `pgsql`。
- `backend/Aura.Api/appsettings*.json`、`start_services.py`：连接串键名全部改为 `PgSql` 并更新占位模板。
- `database/schema.pgsql.sql`：新增 PostgreSQL 版本基础表结构脚本，作为当前主库初始化基线。
- `docker/docker-compose.full.example.yml`、`docker/.env*.example`、`docker/deploy-aura-ubuntu.sh`、`.github/workflows/docker-build-push.example.yml`：容器与 CI 变量由旧关系库体系切换为 PostgreSQL 体系。
- `docs/*`、`README.md`、`开发计划.md`：数据库架构说明统一为 `PostgreSQL + ArangoDB`。

### 未来扩展位

- 当前数据库架构已确认为 `PostgreSQL + ArangoDB`。
- 后续可在 PostgreSQL 侧按需启用 `pgvector + PostGIS`，用于向量近邻检索与空间几何增强场景。

## [0.0.9] - 2026-03-25

### 部署与前端静态资源路径

- **`backend/Aura.Api/Program.cs`**：新增配置项 **`Paths:FrontendRoot`**。若配置非空，则使用该绝对路径作为前端静态根目录（解决仅通过 `ContentRoot` 上溯两级推算 `projectRoot` 时，在「发布目录为单层」或 Docker 镜像内路径与仓库不一致导致的 **`/index/` 404**）。未配置时行为与旧版一致（仍为 `projectRoot/frontend`）。启用显式路径时控制台输出一行中文说明。
- **`backend/Aura.Api/appsettings.json`**：新增 **`Paths:FrontendRoot`** 空字符串占位，便于按环境覆盖。
- **`backend/Aura.Api/appsettings.Production.json`**：将 **`Paths:FrontendRoot`** 设为 **`/opt/aura/frontend`**，与裸机部署到 `/opt/aura` 且前端与 `backend` 同级的目录约定一致。

### Docker 一键联调与 Compose

- **`docker/docker-compose.full.example.yml`**：API 服务增加 **`Paths__FrontendRoot=/app/frontend`** 与 **`../frontend:/app/frontend`** 只读挂载，使镜像内无需内置前端目录即可提供首页；**`ASPNETCORE_ENVIRONMENT`** 改为 **`${ASPNETCORE_ENVIRONMENT:-Development}`**，便于部署脚本写入 **Production** 而本地未配置时仍为 **Development**。

### Ubuntu 一键部署脚本

- **`docker/deploy-aura-ubuntu.sh`**：新增变量 **`ASPNETCORE_ENVIRONMENT_VALUE=Production`**；首次生成 **`.env`** 时写入 **`ASPNETCORE_ENVIRONMENT=Production`**；若沿用旧 **`.env`** 且缺少该键则自动追加，避免升级后仍用默认 **Development**。健康检查增加 **`GET /index/`** HTTP 状态码输出，非 200 时给出 **WARN** 与挂载说明。部署结束打印当前 **`.env`** 中 **`ASPNETCORE_ENVIRONMENT`** 行。

### 环境变量模板

- **`docker/.env.full.example`**：注释说明 **ASPNETCORE_ENVIRONMENT** 在本地联调与 **`deploy-aura-ubuntu.sh`** 中的典型取值差异。

### 前端接口同源化（CSP 兼容）

- **`frontend/*/*.js`**：将页面脚本中的 `const apiBase = "https://localhost:5001";` 统一调整为同源 `const apiBase = "";`，避免生产环境在 `http://<server>:5000` 下被 `Content-Security-Policy` 的 `connect-src 'self'` 拦截。
- 覆盖页面：`login`、`index`、`alert`、`campus`、`capture`、`camera`、`device`、`export`、`floor`、`judge`、`log`、`roi`、`role`、`scene`、`search`、`stats`、`track`、`user`。

## [0.0.8] - 2026-03-24

### Docker 化交付
- 新增 `docker/` 目录并集中收敛容器化资产：`backend.Dockerfile`、`ai.Dockerfile`、`docker-compose.full.example.yml`、`docker-compose.prod.template.yml`、`docker-compose.ops-check.example.yml`。
- 新增容器运行脚本：`up-full`/`down-full`/`check-full`（同时覆盖 PowerShell 与 shell），支持本地一键联调与健康检查。
- 新增镜像分发脚本：`build-images`、`push-images`、`login-registry`、`save-images`、`load-images`（同时覆盖 PowerShell 与 shell），支持私有仓库推送与离线 tar 包迁移。
- 新增环境变量模板：`.env.example`、`docker/.env.full.example`、`docker/.env.prod.example`、`docker/.env.registry.example`，并通过 `.gitignore` 白名单保留示例文件。
- 新增 CI/CD 模板：`.github/workflows/docker-build-push.example.yml`、`docker/Jenkinsfile.docker.example`，支持企业流水线接入。
- 新增 `.dockerignore` 并完善 `README.md`、`docker/README.md` 的跨平台、企业网络、离线迁移与生产模板说明。

### 开发环境账号与数据层修复
- `backend/Aura.Api/Program.cs`：新增开发环境一次性管理员密码重置能力（`Dev:ResetAdminPasswordOnce`），仅在 Development 下生效；可一次性重置并打印新随机密码，随后提示回滚开关。
- `backend/Aura.Api/Program.cs`：在一次性重置成功后，自动回写 `appsettings.Development.json` 将 `Dev:ResetAdminPasswordOnce` 置为 `false`，避免重复触发；回写失败时输出明确提示。
- `backend/Aura.Api/Data/PgSqlStore.cs`：修复 `GetCapturesAsync`、`GetAlertsAsync` 缺失 `@Limit` 绑定参数导致的查询失败。
- `backend/Aura.Api/Data/PgSqlStore.cs`：修复 `GetUsersAsync` 的 Dapper 映射异常（`status` 类型与 `created_at` 类型对齐），避免管理员自动创建时触发用户列表物化失败。
- `backend/Aura.Api/Data/PgSqlStore.cs`：进一步修复 `DbCapture.CaptureTime`、`DbAlert.CreatedAt`、`DbUserListItem.Status` 的物化类型对齐问题，消除用户/抓拍/告警列表查询异常。
- `backend/Aura.Api/Program.cs`：修复统计与导出接口中的匿名类型推断冲突（`DateTime` 与 `DateTimeOffset` 混用导致 `CS0173`），统一将数据库分支映射为 `DateTimeOffset` 后再参与聚合与导出。
- `backend/Aura.Api/appsettings.Development.json`：新增 `Dev:ResetAdminPasswordOnce` 配置项，默认 `false`。
- `README.md`：补充开发环境一次性重置 admin 密码的使用说明。

### 安全加固
- `backend/Aura.Api/Program.cs`：登录接口由后端下发 `aura_token` Cookie（`HttpOnly` + `SameSite=Lax` + 按 HTTPS 自动 `Secure`），前端不再通过 JS 写入 Cookie。
- `backend/Aura.Api/Program.cs`：新增 `POST /api/auth/logout`，由服务端清理 `HttpOnly` Cookie；`frontend/common/shell.js` 登出按钮改为调用后端注销接口。
- `backend/Aura.Api/Program.cs`：抓拍鉴权策略收紧，生产环境下若设备未配置 `nvr_device.hmac_secret`，不再回退全局 `Security:HmacSecret`。

### 工程与文档
- `抓拍链路回归脚本.ps1`、`全系统联调与压测脚本.ps1`：移除内置 `admin123`，改为读取环境变量 `AURA_ADMIN_PASSWORD`。
- `README.md`：删除默认测试密码说明，更新为“开发环境随机强密码 + 环境变量驱动脚本”模式。

### 可观测性
- `backend/Aura.Api/Program.cs`：`PgSqlStore`、`RedisCacheService`、`RetryQueueService` 调整为通过 DI 注入 `ILogger`，统一接入结构化日志能力。
- `backend/Aura.Api/Cache/RetryQueueService.cs`：补充初始化/入队/出队/长度查询失败日志，避免 Redis 异常静默。
- `backend/Aura.Api/Cache/RedisCacheService.cs`：补充初始化、删缓存、释放锁失败日志。
- `backend/Aura.Api/Data/PgSqlStore.cs`：对用户查询、设备写入、抓拍写入、操作日志查询、设备 HMAC 查询、轨迹时间范围查询、抓拍分页查询、虚拟人员写入等关键失败路径补充结构化日志。

### 配置与会话安全
- `backend/Aura.Api/Program.cs`：JWT 鉴权新增 `aura_token` Cookie 读取，支持 API 从 HttpOnly Cookie 完成认证（同时兼容 Authorization 头与 SignalR `access_token`）。
- `backend/Aura.Api/appsettings.json`：移除默认弱密钥与弱连接串，改为显式占位符，避免误用默认配置直接上线。
- `backend/Aura.Api/appsettings.Development.json`：补齐开发环境专用连接串与存储配置，将本地联调配置与通用基线配置分离。
- `backend/Aura.Api/appsettings.Production.json`：`PgSql` 连接串改为显式生产账号/密码策略。

### 前端会话收敛
- `frontend/login/login.js`：移除登录后 token 持久化写入（不再写入 `localStorage`）。
- `frontend/*/*.js`：统一停止从 `localStorage` 读取 token；页面请求继续兼容原 Authorization 头结构，但 token 来源已清空，实际认证走 HttpOnly Cookie。
- `frontend/index/index.js`、`frontend/scene/scene.js`：SignalR 连接注释与行为更新为 Cookie 会话优先（`accessTokenFactory` 仅保留兼容占位）。

### 认证与浏览器安全头收尾
- `frontend/*/*.js`：移除遗留 `Authorization: Bearer ...` 请求头，统一改为 `fetch(..., { credentials: "include" })`，完全使用同域 HttpOnly Cookie 会话。
- `backend/Aura.Api/Program.cs`：新增统一安全响应头中间件，包含 `Content-Security-Policy`、`X-Content-Type-Options`、`X-Frame-Options`、`Referrer-Policy`、`Permissions-Policy`。
- `backend/Aura.Api/Program.cs`：`Content-Security-Policy` 改为可配置读取（`Security:CspPolicy`），并在非开发环境启用 `HSTS`。
- `backend/Aura.Api/appsettings*.json`：新增 `Security:CspPolicy` 配置项；生产默认策略收紧 `connect-src`（不再开放 `ws/wss` 通配）。

### 可观测性补齐（数据库层）
- `backend/Aura.Api/Data/PgSqlStore.cs`：其余数据库访问分支的异常处理统一补齐 `ILogger` 结构化日志（原先大量 `catch { return ... }` 的静默降级点已覆盖），包含设备/抓拍/告警/资源树/楼层/摄像头/ROI/轨迹/研判/角色/用户/虚拟人员等链路。

### 重试队列大对象防护
- `backend/Aura.Api/Program.cs`：AI 失败重试新增 `CaptureRetry:AllowInlineBase64Fallback` 策略开关；当图片落盘失败且未允许回退时，不再把内联 Base64 入重试队列，避免 Redis/网络大对象放大。
- `backend/Aura.Api/appsettings*.json`：新增 `CaptureRetry:AllowInlineBase64Fallback`，默认生产禁用、开发可启用。

### 生产配置 Fail-Fast 再加固
- `backend/Aura.Api/Program.cs`：生产环境启动时新增连接串校验：`PgSql/Redis` 为空或仍为占位值将直接启动失败。
- `backend/Aura.Api/Program.cs`：生产环境检测到 PgSql 连接串包含不允许的连接参数时直接拒绝启动。

### 后台任务标准化
- `backend/Aura.Api/Program.cs`：每日研判定时任务由 `Task.Run` 迁移到标准 `BackgroundService`（`DailyJudgeHostedService`），统一使用宿主生命周期管理与取消令牌。
- `backend/Aura.Api/Program.cs`：新增 `DailyJudgeScheduleState` 作为任务委托桥接，保留原研判执行逻辑、零点窗口触发规则与 Redis 分布式锁防重机制。

### AI 与抓拍链路完善
- `ai/main.py`：新增 `POST /ai/extract-file`，支持 AI 服务直接按图片路径提取特征，减少重试链路对内联 Base64 的依赖。
- `backend/Aura.Api/Ai/AiClient.cs`：新增 `ExtractByPathAsync`，后端可优先通过图片路径调用 AI。
- `backend/Aura.Api/Program.cs`：重试处理优先走 `ImagePath` 提特征，仅在失败且存在 `ImageBase64` 时回退；抓拍成功场景新增图片归档落盘并写入 `capture_record.image_path`（由 `Storage:SaveCaptureImageOnSuccess` 控制）。
- `backend/Aura.Api/appsettings*.json`：新增 `Storage:SaveCaptureImageOnSuccess` 配置项。

### 任务可观测性补充
- `backend/Aura.Api/Program.cs`：每日研判后台任务增加执行耗时日志（`costMs`），用于后续阈值告警与性能评估。

### 开发启动预检
- `start_services.py`：新增开发环境预检（读取 `appsettings.Development.json`），启动前自动校验 `Jwt:Key`、`PgSql`、`Redis`、`Ai:BaseUrl` 是否有效，发现占位值时直接失败并提示修复。

### 运维可用性补充
- `backend/Aura.Api/Program.cs`：新增 `GET /api/ops/readiness` 就绪检查接口（需超级管理员），集中返回 JWT/HMAC/PgSql/Redis/AI 配置就绪状态。
- `frontend/*/*.js`：移除历史遗留 `getToken()` 空函数，统一保持 Cookie 会话实现，降低后续维护误导成本。
- 新增文档：`readiness运维使用说明.md`，包含接口用法、字段解释、上线前检查步骤与常见失败处理。
- `上线就绪检查脚本.ps1`：新增参数模式（`-User`、`-Password`），并保持环境变量兼容（参数优先，环境变量兜底）。

### SignalR 与 AI 主链路收敛
- `backend/Aura.Api/Program.cs`：新增 `BroadcastEventAsync` 并将原 `Clients.All` 广播统一替换为角色分组推送（`role:building_admin`、`role:super_admin`）。
- `backend/Aura.Api/Program.cs`：`EventHub` 增加连接/断开分组维护（按 `ClaimTypes.Role` 自动加入/移出角色组）。
- `backend/Aura.Api/Program.cs`：抓拍主链路 AI 调用改为“优先文件路径提特征，缺失时回退 Base64”，降低主链路大对象传输；AI 成功且不保留归档时会清理临时文件。

### 告警通知抽象层
- 新增 `backend/Aura.Api/Ops/AlertNotifier.cs`：提供 `IAlertNotifier` 抽象与默认实现，支持 Webhook 与本地文件双通道通知（失败不阻断主流程）。
- `backend/Aura.Api/Program.cs`：在抓拍关键词命中、群租/滞留/夜不归宿研判、手动告警等节点接入通知调用。
- `backend/Aura.Api/appsettings*.json`：新增 `Ops:Alert:WebhookUrl` 与 `Ops:Alert:FilePath` 配置项，支持按环境配置通知落地方式。

### 告警通道上线前自检
- `backend/Aura.Api/Program.cs`：新增 `POST /api/ops/alert-notify-test`（仅超级管理员），可主动触发一次标准告警通知并记录操作日志，用于验证 Webhook/文件通知链路可用性。
- `全系统联调与压测脚本.ps1`：新增“告警通知自检”步骤，在联调流程末尾自动调用 `/api/ops/alert-notify-test`，便于上线前一键校验通知通道。

### 告警通道健康度指标
- `backend/Aura.Api/Ops/AlertNotifier.cs`：新增通知统计能力，覆盖总发送次数、Webhook/文件通道成功失败计数、最近失败通道/原因/时间。
- `backend/Aura.Api/Program.cs`：新增 `GET /api/ops/alert-notify-stats`（仅超级管理员），用于实时查看告警通知链路健康度。
- `全系统联调与压测脚本.ps1`：新增通知统计查询步骤，联调后自动输出成功/失败计数与最近失败信息。

### 告警健康阈值接入就绪检查
- `backend/Aura.Api/Program.cs`：`GET /api/ops/readiness` 新增 `alertNotify` 检查项，结合通知统计与“最近失败时间窗口”判断告警通道健康状态。
- `backend/Aura.Api/appsettings*.json`：新增 `Ops:Alert:HealthFailIfRecentFailureMinutes` 配置项（开发默认 `0` 关闭窗口判定，生产默认 `30` 分钟），支持按环境设置发布阻断阈值。

### 上线检查脚本增强（告警健康）
- `上线就绪检查脚本.ps1`：`/api/ops/readiness` 结果解析新增 `alertNotify` 检查项输出与统计信息展示（总量、Webhook/文件成功失败计数）。
- `上线就绪检查脚本.ps1`：当 `alertNotify` 未通过时，最终结果将纳入失败项，并额外输出最近失败通道/原因/时间，便于发布前快速定位通知链路问题。

### 运维文档同步（告警健康字段）
- `readiness运维使用说明.md`：补充 `checks.alertNotify` 与 `data.alertNotify.*` 字段说明、返回示例及阈值含义。
- `readiness运维使用说明.md`：上线前标准流程新增“先触发 `POST /api/ops/alert-notify-test`，再复查 `GET /api/ops/readiness`”步骤，并补充 `alertNotify=false` 的排障建议。

### 联调脚本发布闸门补齐
- `全系统联调与压测脚本.ps1`：新增联调末尾 `GET /api/ops/readiness` 复查步骤，并输出 `alertNotify` 在内的完整检查项结果。
- `全系统联调与压测脚本.ps1`：新增最终结果与退出码约定：`ready=false` 时输出失败项并 `exit 2`，`ready=true` 时 `exit 0`，可直接用于流水线阻断。

### 脚本退出码规范统一
- `上线就绪检查脚本.ps1`、`全系统联调与压测脚本.ps1`：统一退出码语义为 `0=检查通过`、`2=就绪检查未通过`、`3=接口调用或执行异常`。
- `上线就绪检查脚本.ps1`、`全系统联调与压测脚本.ps1`：统一补充 `[RESULT]` 结果行（含 `exit_code`），便于 CI/CD 日志解析与告警编排。

### CI 文档与脚本语法修复
- `readiness运维使用说明.md`：新增“退出码规范与 CI 示例”章节，包含 GitHub Actions 与 Jenkins 的最小可用示例。
- `全系统联调与压测脚本.ps1`：修复并规避编码导致的解析问题（脚本文本改为 ASCII 兼容内容），确保在 Windows PowerShell 下可被正确解析执行。

## [0.0.7] - 2026-03-24

### 安全加固
- `backend/Aura.Api/Program.cs`：生产环境缺失/使用开发占位 JWT/HMAC 时将直接启动失败（fail fast），避免“配置没配好也能跑”。
- `backend/Aura.Api/Program.cs`：登录后门 `admin/admin123` 已移除；开发环境仅在数据库无用户时自动创建 `admin`，并在控制台输出随机强密码（仅开发环境）。
- 抓拍接入链路：`/api/capture/push`、`/api/capture/sdk`、`/api/capture/onvif` 增加统一鉴权（`X-Signature` HMAC + `Security:CaptureIpWhitelist`）与请求体/体积限制，并叠加速率限制。
- SignalR：`EventHub` 启用 `[Authorize]`，连接需携带已认证的 JWT（querystring `access_token`）。
- 上传安全：楼层图上传已禁用 `svg`，仅允许 `png/jpg/jpeg/webp`。
- 输入防滥用：限制 `ImageBase64` 最大长度、`MetadataJson` 最大长度、抓拍请求体上限；向量检索强制 512 维并加入限流。

### 性能与可靠性
- 捕获与输出分页：`/api/capture/list`（含分页模式）与 `/api/output/events` 等查询改为 SQL 范围/分页，减少固定小 `LIMIT` 导致的截断与漏数风险。
- AI 失败重试：默认将重试图片落盘到 `storage/uploads/capture-retry`，队列中优先保留图片路径，降低 Redis/网络中大 Base64 的体积压力（仍保留 `CaptureRetry:PreferInlineBase64` 的可选兜底）。
- 定时研判：每日研判任务使用 `ApplicationStopping` 取消令牌，并通过 Redis 分布式锁避免多实例重复执行。

## [0.0.6] - 2026-03-23

### 数据

- `database/schema.pgsql.sql`：PostgreSQL 库默认表结构；用于初始建库建表与字段/约束基线（默认库名 `aura`，可按环境调整）
- 全部业务表补充**表级注释**与**字段级注释**（`COMMENT`），便于 DBA 与研发对照维护

### 修复

- `backend/Aura.Api/Data/PgSqlStore.cs`：`alert_record.detail_json` 列为 `JSONB` 类型时，原先用普通文本直接写入可能导致 PostgreSQL 拒绝插入或静默失败；改为插入时使用 `to_jsonb`，查询时使用 `jsonb_typeof`/`cast` 等方式与库表类型一致

### 说明

- **已有库表**：`CREATE TABLE IF NOT EXISTS` 不会为已存在的表补注释或改字符集；需调整时请使用 `ALTER DATABASE` / `ALTER TABLE` 或在新环境执行完整脚本
- 回归验证：在 API 已启动前提下，`抓拍链路回归脚本.ps1` 与含「异常」关键词的模拟抓拍场景下，抓拍、操作日志与告警链路可正常跑通（向量条数依赖 AI 与向量服务，可能为 0）

### 前端交互优化
- 统一业务页状态反馈：移除各页面“等待查询/等待操作/等待检索/加载中...”默认长占位文案；请求失败则常驻错误提示（高亮），请求成功则短暂显示成功/结果信息并约 5 秒后自动清空。
- 新增全局状态样式：`frontend/common/shell.css` 增加 `.aura-status`（含错误态 `.is-error`），用于多页面复用一致的提示外观。
- 页面调整范围：`alert、device、capture、campus、role、floor、log、track、judge、user、search、index、roi` 等页面的 `html/css/js` 默认提示渲染逻辑已统一。
- 主题切换交互调整：顶部主题切换由下拉选择改为“单一图标按钮点击切换”，点击在浅色/深色之间切换，并保持与 `theme-pref` 的持久化一致。
- 登录页视觉留白优化：提升登录页面整体上下空间（外层与卡片上下 padding），避免界面过于紧凑、提升居中观感。

### 回归说明
- 手工触发各页面查询/创建/更新/检索等操作：验证成功提示会自动消失，失败提示保持可见；并核对项目中不再存在用于展示的“等待查询/等待操作/等待检索/加载中”长占位文案。

## [0.0.5] - 2026-03-21

### 新增

- ECharts 统计驾驶舱数据接口与图表页面（趋势、设备分布、告警类型）
- 报表导出能力（`csv/xlsx`，支持 `capture/alert/judge` 数据集）
- 外部输出接口增强（事件流分页与时间过滤、人员归属筛选）
- 全系统联调与压测脚本：`全系统联调与压测脚本.ps1`
- 部署与运维文档：`部署文档与运维手册.md`、`上线检查清单.md`

### 变更

- `README.md` 更新为第五阶段完成态
- 增加生产配置模板：`backend/Aura.Api/appsettings.Production.json`

## [0.0.4] - 2026-03-21

### 新增

- 归寝/群租异常滞留/夜不归宿三类研判逻辑与接口
- 每日零点自动研判任务
- SignalR 实时事件推送（抓拍、告警、轨迹、研判）
- Three.js 3D 楼宇白模、跨层告警闪点、3D-2D 下钻切换
- 态势事件流面板、以图搜轨页面、2D 轨迹动画播放器

### 数据

- 新增研判结果表：`judge_result`

## [0.0.3] - 2026-03-21

### 新增

- 资源树 CRUD（园区/楼栋/楼层/房间）
- 楼层平面图上传与展示
- Canvas 摄像头点位拖拽布置
- Canvas ROI 多边形绘制编辑器
- ROI ↔ 房间映射管理
- 过镜事件与 ROI 空间碰撞判定引擎

### 前端

- 新增空间配置页面：`campus`、`floor`、`camera`、`roi`
- 轨迹页面接入真实轨迹事件数据

## [0.0.2] - 2026-03-21

### 新增

- `ICaptureAdapter` 统一抓拍标准结构
- 海康 ISAPI / C++ SDK / ONVIF 三通道接入
- NVR 设备注册与管理
- Python FastAPI AI 服务（提特征、向量检索）
- Milvus 向量写入/检索（含内存回退）
- 抓拍入库 -> AI 提取 -> 向量检索闭环
- 无监督聚类生成 `Virtual_Person_ID`
- Redis 死信队列与失败重试

### 文档与测试

- 抓拍链路端到端测试清单：`抓拍链路端到端测试清单.md`
- 抓拍链路回归脚本：`抓拍链路回归脚本.ps1`
- C++ SDK 契约文档：`C++SDK对接接口契约与待接入点.md`

## [0.0.1] - 2026-03-21

### 新增

- .NET Core WebAPI 项目脚手架与基础路由
- PostgreSQL 初始表结构与基础字典
- JWT 鉴权中间件与 RBAC 权限框架
- 系统用户与角色管理能力
- 操作日志基础框架
- Redis 连接与基础缓存层
- 前端基础看板与页面目录规范（同名 `html/css/js`）

---

## 版本规范

- 版本号遵循 `MAJOR.MINOR.PATCH`
- 当前版本：`0.2.0`
