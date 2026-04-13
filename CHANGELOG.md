# 更新日志

本文档用于记录寓瞳系统各版本功能演进与交付内容。

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
- 当前版本：`0.1.9`
