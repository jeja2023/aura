# 运维脚本入口

本目录收纳项目运维与回归脚本，根目录不再直接堆放脚本文件。

## 统一入口

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 readiness
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 ai-check
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 ai-eval -SummaryOnly -MinRecall 0.9 -MinMrr 0.9
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 capture-regression
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 full-check
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-status
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-backup
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-migrate
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-rollback -BackupFile .\artifacts\db-backups\aura-prod.dump -ConfirmRestore -Clean -IfExists
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 db-rollback-migrate -BackupFile .\artifacts\db-backups\aura-prod.dump -ConfirmRestore -Clean -IfExists
```

## 子脚本

- `readiness-check.ps1`：上线前健康与 readiness 检查。
- `ai-check.ps1`：AI live/ready 与检索审计巡检。
- `ai-eval.ps1`：AI 检索离线评测，支持标注集路径和 `-MinRecall`、`-MinMrr`、`-MaxEmptyRate` 等质量阈值。
- `capture-regression.ps1`：抓拍、查询、向量检索与重试队列回归。
- `full-check.ps1`：登录、模拟抓拍、研判与输出联调。
- `db-maintenance.ps1`：PostgreSQL 迁移状态、迁移前备份、备份校验与恢复包装。

数据库脚本默认读取 `ConnectionStrings__PgSql`，也可显式传 `-ConnectionString`。发布包环境建议设置 `AURA_DB_MIGRATOR_DLL` 指向已发布的 `Aura.DbMigrator.dll`；源码环境会回退 `dotnet run`。恢复与回滚必须显式追加 `-ConfirmRestore`。

自动迁移边界：Docker Compose 会通过 `db-migrate` 服务在 API 前自动执行迁移；本机 `python start_services.py` 也会自动执行迁移；直接 `dotnet run --project backend/Aura.Api` 不会自动迁移，请先运行 `db-status` / `db-migrate`。

`db-rollback` 表示“校验备份 -> 恢复备份 -> 校验迁移历史”；`db-rollback-migrate` 会在恢复后继续执行 `migrate`，用于把数据库回到备份点后再前滚到当前发布包版本。
