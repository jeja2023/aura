# 2026-07-03 修复与优化记录

本轮修复目标是收敛已有实现中的风险点，不扩展新的大型功能。

## 已落地

- 后端数据库分页查询增加成功状态：抓拍、告警、操作日志、系统日志、用户列表的分页查询不再把数据库异常伪装成空数据。
- 后端 API 错误语义收紧：PG 已配置时，关键列表、导出、输出、统计接口遇到数据库查询失败会返回 503/500 语义，而不是 HTTP 200 + 空列表。
- 前端静态资源覆盖层：新增 `frontend-overrides`，后端优先读取覆盖文件，再回退到 `frontend` 原文件，用于在不改动原始前端目录时修复小范围问题。
- 前端 HTML 转义兜底：`frontend-overrides/extensions/extensions.js` 中 `window.aura.escapeHtml` 缺失时也会执行安全转义。
- Docker GPU 网络预检：新增 `docker-gpu-preflight.ps1` 与 `docker-gpu-preflight.sh`。默认只检查外部网络 `gpu-bridge`，显式传 `-Create` / `--create` 才创建。
- 依赖基线：后端核心 ASP.NET/OpenTelemetry 包已升级到当前 10.0.9 / 1.16.0 系列；前端生产依赖审计为 0 个漏洞，剩余漏洞在 ESLint 开发工具链。

## 运维恢复命令

审批服务或本机权限恢复后，建议优先执行：

```powershell
# Docker GPU 网络检查；缺失时显式创建
powershell -ExecutionPolicy Bypass -File .\docker-gpu-preflight.ps1
powershell -ExecutionPolicy Bypass -File .\docker-gpu-preflight.ps1 -Create

# 后端编译，必要时把中间产物放在可写目录
$env:AURA_INTERMEDIATE_ROOT = "E:\Aura\generated\.msbuild\"
dotnet restore backend\Aura.Api\Aura.Api.csproj
dotnet build backend\Aura.Api\Aura.Api.csproj --no-restore

# 前端依赖修复与审计
cd frontend
npm audit fix --package-lock-only
npm audit --package-lock-only
npm audit --omit=dev --package-lock-only
```

Linux / macOS Docker 网络检查：

```bash
sh ./docker-gpu-preflight.sh
sh ./docker-gpu-preflight.sh --create
```

## 验证恢复记录

- 审批服务恢复后，已删除遗留临时产物：`_write_test_root.txt`、`generated/project-preprocess.xml`、`generated/.msbuild`。
- 前端 ESLint 开发工具链已升级到 `eslint` / `@eslint/js` 9.39.4，`npm audit --json` 显示 0 个漏洞，`npm run lint` 通过。
- NuGet 漏洞审计已恢复。`Microsoft.AspNetCore.OpenApi` 10.0.9 传递依赖 `Microsoft.OpenApi` 2.0.0 命中 GHSA-v5pm-xwqc-g5wc，已显式覆盖为 `Microsoft.OpenApi` 2.7.5；`dotnet list Aura.sln package --vulnerable --include-transitive` 显示所有项目无易受攻击包。
- 0.1.31 最终验证已通过：`dotnet build backend\Aura.Api\Aura.Api.csproj --no-restore -v:minimal /m:1` 为 0 warning / 0 error；`Aura.Api.Tests` 45/45；`Aura.Api.Integration.Tests` 42/42；AI pytest 32/32；前端 `npm run lint` 通过；NuGet 漏洞审计未发现易受攻击包。
- 修复了 generated 静态资源管线：只有 `frontendRoot` 存在时才创建 `PhysicalFileProvider`，避免隔离 content root 的集成测试在缺少 `frontend` 目录时启动失败。

## AI `/ai/extract-file` 路径策略

代码中已有 `AURA_AI_EXTRACT_FILE_ROOTS` 白名单机制。生产和 Docker 部署建议配置为容器内共享图片目录，例如：

```env
AURA_AI_EXTRACT_FILE_ROOTS=/app/storage/captures
```

如果使用外部图片特征服务，仍建议保证 API、AI worker 和外部服务对图片路径有一致的挂载视图；否则优先改用 `/ai/extract` 的 Base64 模式。

## 0.1.31 发布补记
- 构建源已收敛到 `backend/Aura.Api` 正式源码，`generated/` 仅保留为审查/生成产物，不再作为生产编译来源。
- 本轮新增的反向代理头、Cookie Secure、HMAC 常量时间校验、CIDR 白名单、Redis 复用/降级、AI 图片输入限制和前端安全渲染均已写入 `CHANGELOG.md` 与 README。
- 生产上线时请以 `v0.1.31` 镜像标签或现场自定义等价标签为准，并按 `docs/运维上线手册.md` 的 0.1.31 release checklist 复核。
