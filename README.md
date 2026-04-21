# 寓瞳系统（第五阶段完成态）

本仓库已按《开发计划.md》《开发规范.md》完成第一至第五阶段开发，覆盖接入网关、AI 特征链路、空间引擎、业务研判、3D/2D 态势、统计导出与外联输出。

## 项目状态

- 当前版本：`0.1.20`（细目见 **`CHANGELOG.md`**）
- 阶段状态：第一至第五阶段均已验收通过
- 交付结论：计划项已全部完成并在 `开发计划.md` 归档勾选
- 工程状态：后端可构建（推荐打开根目录 **`Aura.sln`** 或 `dotnet build backend/Aura.Api/Aura.Api.csproj`）、前端页面可访问、核心链路可联调
- 运维状态：已提供回归脚本、联调压测脚本、部署与上线检查文档
- 变更记录：见根目录 **`CHANGELOG.md`**（`0.1.2` 起补充后端模块化、DI 与编码修复等说明；`0.1.17` 起补充海康 alertStream、媒体规划 API、设备/联调前端与海康表单布局等；`0.1.19` 为数据库迁移工具化、统一错误响应、安全扫描与回归测试补齐等综合迭代）

## 目录结构

- `Aura.sln`：Visual Studio / Rider 解决方案入口
- `Directory.Build.props`：统一 MSBuild 中间输出路径（`.verify_build\obj`）并排除误编译 `obj` 生成物，便于本机工具链
- `backend/Aura.Api`：.NET 10 WebAPI 中枢服务；启动入口为 **`Program.cs`**，服务注册在 **`Extensions/ServiceExtensions.cs`**，路由按域拆分在 **`Extensions/AuraEndpoints*.cs`**，安全头与前端路由中间件在 **`Middleware/`**
- `backend/Aura.Api.Tests`：轻量自检工程（聚类/导出等），可选执行
- `backend/Aura.Api.Integration.Tests`：xUnit 集成测试（`WebApplicationFactory`，环境为 `Testing`）。**维护提示**：若修改 `backend/Aura.Api/appsettings.Testing.json` 中的 **`Jwt:Key` / `Jwt:Issuer` / `Jwt:Audience`**，必须同步修改 **`backend/Aura.Api.Integration.Tests/TestingJwt.cs`** 内同名常量，否则 `dotnet test` 会失败。
- `ai`：Python FastAPI AI 服务（特征提取/检索），主入口 `main.py` 已收敛为应用装配入口，核心拆分为 `app/`（启动装配/生命周期/中间件）、`routes/`（API 路由）、`vector_store/`（向量索引存取）、`services/`（Arango/推理/聚类能力）、`models/`（请求模型）
- `database/schema.pgsql.sql`：PostgreSQL 表结构
- `frontend`：Vanilla JS 前端页面（根目录含 **`package.json`**，维护者可执行 **`npm ci`** 与 **`npm run lint`** 做 ESLint 检查）。**NVR 设备**与**海康 ISAPI 联调**分别对应 `frontend/device/` 与 `frontend/device-diag/`（入口见下文「关键页面入口」）
- `deploy/k8s`：Kubernetes 示例（Ingress 拒绝公网 **`/metrics`**、NetworkPolicy 入站基线）与说明文档
- `抓拍链路端到端测试清单.md`：抓拍链路测试清单
- `抓拍链路回归脚本.ps1`：抓拍链路回归脚本
- `全系统联调与压测脚本.ps1`：全系统联调与压测脚本
- `docs/部署文档与运维手册.md`：部署与运维手册
- `最终交付清单.md`：最终交付范围清单
- `docs/上线检查清单.md`：上线前检查清单

## 已落地核心能力

- 认证与权限：JWT + RBAC（超级管理员/楼栋管理员）
- 多协议抓拍接入：海康 ISAPI、ONVIF、C++ SDK
- 抓拍链路：抓拍入库、AI 提特征、向量检索、重试队列
- 空间引擎：楼层图、摄像头点位、ROI 编辑、空间碰撞、轨迹事件
- 业务研判：归寝、群租/异常滞留、夜不归宿
- 态势能力：SignalR 实时事件流，Three.js 3D 白模与 2D 切片下钻
- 统计与报表：ECharts 驾驶舱、CSV/XLSX 导出
- 外联输出：事件流与人员归属输出接口（含分页/筛选）

## 快速启动

### 1) 初始化数据库

导入 `database/schema.pgsql.sql` 到 PostgreSQL 16+。

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

### 3) 启动后端服务

```bash
cd backend/Aura.Api
dotnet run
```

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

### 本机一键启动与就绪检查

建议使用根目录一键脚本完成本机联调与就绪检查：

```powershell
cd e:\Aura
python start_services.py
```

说明：
- **适用范围**：本机 **AI + .NET + PostgreSQL + Redis** 全栈联调；与仅跑 `dotnet test` 的 **`Testing` 环境**（可无 Redis/PG）不同。
- 脚本会优先读取根目录 `e:\Aura\.env`，在启动过程中轮询：**AI 根路径**须 **HTTP 2xx** 且 JSON **`code=0` 且 `model_loaded=true`**；**.NET** 须 **`GET /api/health`** 为 **2xx** 且 **`code=0`** 且 `msg` 含「寓瞳」（避免误将 404 等响应当作就绪）。
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

### Docker 化建议（脚本/巡检任务）

- 示例文件：`docker/docker-compose.ops-check.example.yml`
- 使用方式：
  1. `cp .env.example .env`（Windows 可手动复制并重命名）
  2. 在 `.env` 中填入真实 `AURA_ADMIN_PASSWORD`
  3. `docker compose -f docker/docker-compose.ops-check.example.yml run --rm ops-check`
- 建议：生产环境优先使用 CI/CD Secret 或容器编排 Secret（如 Kubernetes Secret），避免明文进入镜像和仓库。

## 关键页面入口

- 首页看板：`frontend/index/index.html`
- NVR 设备管理（含海康 ISAPI 诊断面板）：`frontend/device/device.html`
- 设备联调（独立海康 ISAPI 联调页）：`frontend/device-diag/device-diag.html`
- 三维态势：`frontend/scene/scene.html`
- 统计驾驶舱：`frontend/stats/stats.html`
- 报表导出：`frontend/export/export.html`
- 以图搜轨：`frontend/search/search.html`

## 关键接口（示例）

- 存活探针（负载均衡/K8s）：`GET /api/health/live`
- 业务健康（中文提示）：`GET /api/health`
- AI 健康检查：`GET /`（返回 `code/msg` 与 `model_loaded`，并新增 `熔断状态`、`限流状态`、`回填状态` 三个可视化字段；同时保留 `retrieval_guard`、`backfill_state` 结构化对象）
- AI 检索审计日志：`GET /ai/search-audit-logs?limit=100`（结构化 JSON，`data.items` 每条包含 `time/request_id/success/status/reason/hit_count/latency_ms/engine/strategy/filters_applied/warnings`）
- Prometheus 抓取（可选）：`GET /metrics`，由配置 **`Ops:Metrics:ExposePrometheus`** 控制（默认 `true`；集成测试所用 **`Testing`** 环境为 `false`）。生产环境建议仅允许监控网络或反向代理访问该路径；按路径在公网 Ingress 上拒绝的示例见 **`deploy/k8s/ingress-nginx-deny-public-metrics.example.yaml`**。
- OpenTelemetry 链路追踪（可选）：配置 **`Ops:Telemetry:EnableTracing`** 为 **`true`** 且设置 **`Ops:Telemetry:OtlpEndpoint`**（或环境变量 **`OTEL_EXPORTER_OTLP_ENDPOINT`**）；默认关闭。协议 **`Ops:Telemetry:OtlpProtocol`** 支持 **`Grpc`**（默认）与 **`HttpProtobuf`**。
- AI 服务访问控制（可选）：AI 进程读取 **`AURA_API_KEY`** 时，除根路径健康检查与 OpenAPI 文档外须在请求头携带 **`X-Aura-Ai-Key`**；.NET 侧配置 **`Ai:ApiKey`** 后由命名 **`HttpClient` 自动附加同名请求头。
- 登录：`POST /api/auth/login`
- 媒体规划（不代理音视频，仅能力/路径模板）：`GET /api/media/capabilities`、`POST /api/media/hikvision/stream-hint`
- 海康告警长连接状态（联调用）：`GET /api/device/hikvision/alert-stream-status`
- 抓拍接入：`POST /api/capture/push|sdk|onvif`
- 空间碰撞：`POST /api/space/collision/check`
- 研判执行：`POST /api/judge/run/daily`
- 统计驾驶舱：`GET /api/stats/dashboard`
- 导出：`GET /api/export/{type}?dataset=capture|alert|judge`
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

- 抓拍链路回归：`powershell -ExecutionPolicy Bypass -File "e:\Aura\抓拍链路回归脚本.ps1"`
- 全系统联调压测：`powershell -ExecutionPolicy Bypass -File "e:\Aura\全系统联调与压测脚本.ps1"`
- AI 检索巡检：`powershell -ExecutionPolicy Bypass -File "e:\Aura\AI检索巡检脚本.ps1"`（检查 `熔断状态/限流状态/回填状态` 与检索审计日志；可通过 `-MaxLatencyMs`、`-MinRemainingQuota` 调整阈值）
- AI 检索巡检（CI JSON 模式）：`powershell -ExecutionPolicy Bypass -File "e:\Aura\AI检索巡检脚本.ps1" -JsonOutput`（仅输出结构化 JSON，便于流水线解析；退出码仍为 `0/2/3`）

## 部署建议

- 参考 `backend/Aura.Api/appsettings.Production.json` 填充生产配置，并务必设置 **`AllowedHosts`** 为实际域名
- 参考 `docs/部署文档与运维手册.md` 与 `docs/上线检查清单.md` 执行上线流程
- Docker 化参考：`docker/README.md`（含 `full` 联调、`ops-check` 巡检与生产模板）
- Kubernetes：`deploy/k8s/README.md` 说明 NetworkPolicy 与按路径限制 **`/metrics`** 的关系，并提供 ingress-nginx 与 NetworkPolicy 示例清单
