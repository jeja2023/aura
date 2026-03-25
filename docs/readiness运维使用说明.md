# Readiness 运维使用说明

本文档用于指导运维与上线负责人使用 `readiness` 接口进行上线前自检与日常巡检。

---

## 1. 接口说明

- 路径：`GET /api/ops/readiness`
- 鉴权：需要“超级管理员”权限
- 目的：集中检查中枢服务关键配置是否就绪

返回示例：

```json
{
  "code": 0,
  "msg": "就绪检查通过",
  "data": {
    "environment": "Production",
    "ready": true,
    "checks": {
      "jwt": true,
      "hmac": true,
      "pgsql": true,
      "redis": true,
      "ai": true,
      "alertNotify": true
    },
    "alertNotify": {
      "healthFailIfRecentFailureMinutes": 30,
      "hasRecentFailure": false,
      "recentWindowStart": "2026-03-24T12:04:56+08:00",
      "stats": {
        "totalNotify": 128,
        "webhookSuccess": 120,
        "webhookFailure": 1,
        "fileSuccess": 128,
        "fileFailure": 0,
        "lastFailureChannel": "webhook",
        "lastFailureReason": "HTTP 500",
        "lastFailureAt": "2026-03-24T11:50:10+08:00"
      }
    }
  },
  "time": "2026-03-24T12:34:56+08:00"
}
```

---

## 2. 字段解释

- `data.environment`：当前运行环境（Development / Production）
- `data.ready`：总就绪状态（所有检查项均为 `true` 时为 `true`）
- `data.checks.jwt`：JWT 配置是否有效（非空、非占位、非开发弱值）
- `data.checks.hmac`：HMAC 配置是否有效（非空、非占位、非开发弱值）
- `data.checks.pgsql`：PostgreSQL 连接串是否有效（非空、非占位）
- `data.checks.redis`：Redis 连接串是否有效（非空、非占位）
- `data.checks.ai`：AI 服务地址是否已配置
- `data.checks.alertNotify`：告警通知通道健康检查结果（按最近失败窗口判定）
- `data.alertNotify.healthFailIfRecentFailureMinutes`：最近失败判定窗口（分钟），`0` 表示关闭该判定
- `data.alertNotify.hasRecentFailure`：窗口内是否出现过通知失败
- `data.alertNotify.stats`：通知统计快照（总量、各通道成功/失败、最近一次失败信息）

---

## 3. 上线前标准检查步骤

1. 启动 AI 服务与 .NET 中枢服务（Production 配置）。
2. 使用超级管理员账号登录，获取会话（HttpOnly Cookie）。
3. 调用 `GET /api/ops/readiness`：
   - 若 `ready=true`：通过配置就绪检查；
   - 若 `ready=false`：必须先修复 `checks` 中为 `false` 的项，再继续上线流程。
4. 调用 `POST /api/ops/alert-notify-test` 发送一条测试通知，验证 Webhook/文件通知链路。
5. 再次调用 `GET /api/ops/readiness`，确认 `checks.alertNotify=true` 且 `hasRecentFailure=false`（或符合当前阈值策略）。
6. 调用 `GET /api/health` 确认服务健康。
7. 抽样调用核心接口（建议至少覆盖抓拍、研判、导出、SignalR 连接）。

---

## 4. 常见失败与处理建议

- `jwt=false`
  - 检查 `Jwt:Key` 是否仍为占位值或开发弱值。
- `hmac=false`
  - 检查 `Security:HmacSecret` 是否仍为占位值或开发弱值。
- `pgsql=false`
  - 检查 `ConnectionStrings:PgSql` 是否为空/占位；
  - 检查连接串中的主机、端口、库名和账号密码是否正确。
- `redis=false`
  - 检查 `ConnectionStrings:Redis` 是否为空/占位；
  - 确认网络策略与认证参数正确。
- `ai=false`
  - 检查 `Ai:BaseUrl` 是否为空或地址不可达；
  - 校验 AI 进程监听与防火墙策略。
- `alertNotify=false`
  - 查看 `data.alertNotify.stats.lastFailureChannel / lastFailureReason / lastFailureAt`；
  - 检查 `Ops:Alert:WebhookUrl` 是否可达、鉴权参数是否正确；
  - 检查 `Ops:Alert:FilePath` 目录权限与磁盘空间；
  - 根据环境策略调整 `Ops:Alert:HealthFailIfRecentFailureMinutes`（如开发环境可设为 `0`，生产环境建议保持正值）。

---

## 5. 建议纳入自动化

- 将 `readiness` 纳入发布流水线“发布前检查”步骤。
- 将 `ready=false` 作为发布阻断条件。
- 将 `POST /api/ops/alert-notify-test` 纳入流水线，触发后再检查 `readiness` 的 `alertNotify` 项。
- 与 `api/health` 组合使用：`health` 判断进程存活，`readiness` 判断配置可用。
- 本机联调可使用根目录一键脚本 `start_services.py`：启动成功后会自动登录“超级管理员”，并调用 `GET /api/ops/readiness` 输出 `[readiness] ready=...`。如用于 CI 预检，可执行 `python start_services.py --run-until-ready`。

---

## 6. 备注

- `readiness` 主要验证“配置就绪性”，不替代业务联调与压测。
- 建议与《上线检查清单》联合执行，保留检查记录以便审计追溯。

---

## 7. 退出码规范与 CI 示例

当前脚本 `上线就绪检查脚本.ps1`、`全系统联调与压测脚本.ps1` 已统一退出码：

- `0`：检查通过，可继续发布流程
- `2`：检查未通过（存在失败检查项），应阻断发布
- `3`：接口调用失败或脚本执行异常，应阻断发布并排查环境/网络/鉴权

脚本会输出统一结果行，便于日志解析：

- 成功：`[RESULT] READY. exit_code=0 ...`
- 失败：`[RESULT] NOT READY. exit_code=2 ...`
- 异常：`[RESULT] ERROR. exit_code=3 ...`

### 7.1 GitHub Actions 示例

```yaml
- name: Run go-live readiness check
  shell: pwsh
  run: |
    $env:AURA_ADMIN_USER = "admin"
    $env:AURA_ADMIN_PASSWORD = "${{ secrets.AURA_ADMIN_PASSWORD }}"
    ./上线就绪检查脚本.ps1
```

说明：GitHub Actions 会自动以脚本退出码作为步骤结果，`2/3` 会使步骤失败并阻断后续发布阶段。

### 7.2 Jenkins（PowerShell）示例

```powershell
$env:AURA_ADMIN_USER = "admin"
$env:AURA_ADMIN_PASSWORD = $env:AURA_ADMIN_PASSWORD
powershell -NoProfile -ExecutionPolicy Bypass -File ".\全系统联调与压测脚本.ps1"
if ($LASTEXITCODE -ne 0) {
    throw "Pipeline blocked by readiness gate. exit_code=$LASTEXITCODE"
}
```

说明：建议在流水线中保留脚本原始控制台输出，便于根据 `[RESULT]` 及失败项快速定位问题。
