# Aura 0.3.0 商业发布门禁报告

- 结论：`blocked`
- Git commit：`bc2f9b57975fbad41911e9e3986b8022026738f7`
- 迁移版本：`036`
- 服务画像：`private-standard-target` v1
- 生成时间：`2026-07-24T02:47:33.343095+00:00`

## 环境断言

- 已批准服务画像：`false`
- 真实依赖：`false`
- 目标硬件：`false`
- 秘密扫描通过：`[redacted]`

## 检查结果

| 检查 | 状态 | 退出码 | 证据 |
| --- | --- | ---: | --- |
| `dotnet_tests` | `passed` | 0 | artifacts/release-gate-local/dotnet_tests.log |
| `python_tests` | `passed` | 0 | artifacts/release-gate-local/python_tests.log |
| `frontend_lint` | `passed` | 0 | artifacts/release-gate-local/frontend_lint.log |
| `postgres_migrations` | `passed` | 0 | artifacts/release-gate-local/postgres_migrations.log |
| `arango_real` | `blocked` | None | - |
| `pgvector_target_scale` | `blocked` | None | - |
| `backlog_recovery` | `blocked` | None | - |
| `backup_restore` | `blocked` | None | - |
| `upgrade_rollback` | `blocked` | None | - |
| `browser_matrix` | `blocked` | None | - |
| `linux_scripts` | `blocked` | None | - |
| `security_privacy` | `blocked` | None | - |
| `oidc_idp` | `blocked` | None | - |
| `real_device_adapter` | `blocked` | None | - |

## 服务画像错误

- service profile status must be approved
- service profile approval is missing reference
- service profile approval is missing approvedBy
- service profile approval is missing approvedAt

## 判定说明

全部 14 项检查必须为 `passed`，且服务画像、真实依赖、目标硬件和秘密扫描断言均成立。缺失的现场证据保持为 `blocked`，不得用本机 smoke 结果替代。
