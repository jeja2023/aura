# 更新日志

本文档用于记录寓瞳系统各版本功能演进与交付内容。

---

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
- `backend/Aura.Api/Data/MySqlStore.cs`：修复 `GetCapturesAsync`、`GetAlertsAsync` 缺失 `@Limit` 绑定参数导致的查询失败。
- `backend/Aura.Api/Data/MySqlStore.cs`：修复 `GetUsersAsync` 的 Dapper 映射异常（`status` 类型与 `created_at` 类型对齐），避免管理员自动创建时触发用户列表物化失败。
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
- `backend/Aura.Api/Program.cs`：`MySqlStore`、`RedisCacheService`、`RetryQueueService` 调整为通过 DI 注入 `ILogger`，统一接入结构化日志能力。
- `backend/Aura.Api/Cache/RetryQueueService.cs`：补充初始化/入队/出队/长度查询失败日志，避免 Redis 异常静默。
- `backend/Aura.Api/Cache/RedisCacheService.cs`：补充初始化、删缓存、释放锁失败日志。
- `backend/Aura.Api/Data/MySqlStore.cs`：对用户查询、设备写入、抓拍写入、操作日志查询、设备 HMAC 查询、轨迹时间范围查询、抓拍分页查询、虚拟人员写入等关键失败路径补充结构化日志。

### 配置与会话安全
- `backend/Aura.Api/Program.cs`：JWT 鉴权新增 `aura_token` Cookie 读取，支持 API 从 HttpOnly Cookie 完成认证（同时兼容 Authorization 头与 SignalR `access_token`）。
- `backend/Aura.Api/appsettings.json`：移除默认弱密钥与弱连接串，改为显式占位符，避免误用默认配置直接上线。
- `backend/Aura.Api/appsettings.Development.json`：补齐开发环境专用连接串与存储配置，将本地联调配置与通用基线配置分离。
- `backend/Aura.Api/appsettings.Production.json`：`MySql` 连接串改为 `AllowPublicKeyRetrieval=False`。

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
- `backend/Aura.Api/Data/MySqlStore.cs`：其余数据库访问分支的异常处理统一补齐 `ILogger` 结构化日志（原先大量 `catch { return ... }` 的静默降级点已覆盖），包含设备/抓拍/告警/资源树/楼层/摄像头/ROI/轨迹/研判/角色/用户/虚拟人员等链路。

### 重试队列大对象防护
- `backend/Aura.Api/Program.cs`：AI 失败重试新增 `CaptureRetry:AllowInlineBase64Fallback` 策略开关；当图片落盘失败且未允许回退时，不再把内联 Base64 入重试队列，避免 Redis/网络大对象放大。
- `backend/Aura.Api/appsettings*.json`：新增 `CaptureRetry:AllowInlineBase64Fallback`，默认生产禁用、开发可启用。

### 生产配置 Fail-Fast 再加固
- `backend/Aura.Api/Program.cs`：生产环境启动时新增连接串校验：`MySql/Redis` 为空或仍为占位值将直接启动失败。
- `backend/Aura.Api/Program.cs`：生产环境检测到 MySQL 连接串包含 `AllowPublicKeyRetrieval=True` 时直接拒绝启动。

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
- `start_services.py`：新增开发环境预检（读取 `appsettings.Development.json`），启动前自动校验 `Jwt:Key`、`MySql`、`Redis`、`Ai:BaseUrl` 是否有效，发现占位值时直接失败并提示修复。

### 运维可用性补充
- `backend/Aura.Api/Program.cs`：新增 `GET /api/ops/readiness` 就绪检查接口（需超级管理员），集中返回 JWT/HMAC/MySQL/Redis/AI 配置就绪状态。
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

- `database/schema.sql`：库默认使用 `utf8mb4` + `utf8mb4_unicode_ci`（完整 Unicode）；增加 `CREATE DATABASE IF NOT EXISTS` / `USE`（默认库名 `aura`，可按环境调整）
- 全部业务表补充**表级注释**与**字段级注释**（`COMMENT`），便于 DBA 与研发对照维护

### 修复

- `backend/Aura.Api/Data/MySqlStore.cs`：`alert_record.detail_json` 列为 `JSON` 类型时，原先用普通文本直接写入可能导致 MySQL 拒绝插入或静默失败；改为插入时使用 `JSON_QUOTE`，查询时使用 `COALESCE(JSON_UNQUOTE(detail_json), …)`，与库表类型一致

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
- MySQL 初始表结构与基础字典
- JWT 鉴权中间件与 RBAC 权限框架
- 系统用户与角色管理能力
- 操作日志基础框架
- Redis 连接与基础缓存层
- 前端基础看板与页面目录规范（同名 `html/css/js`）

---

## 版本规范

- 版本号遵循 `MAJOR.MINOR.PATCH`
- 当前版本：`0.0.8`
- 当前版本序列：`0.0.1` -> `0.0.2` -> `0.0.3` -> `0.0.4` -> `0.0.5` -> `0.0.6` -> `0.0.7` -> `0.0.8`
