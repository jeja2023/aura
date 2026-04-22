# 更新日志

本文档记录仓库关键版本与阶段性改动，便于联调、回归与发布追踪。

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
- 当前版本：`0.1.21`
