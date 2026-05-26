# 运维脚本入口

本目录收纳项目运维与回归脚本，根目录不再直接堆放脚本文件。

## 统一入口

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 readiness
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 ai-check
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 capture-regression
powershell -ExecutionPolicy Bypass -File .\scripts\ops\aura-ops.ps1 full-check
```

## 子脚本

- `readiness-check.ps1`：上线前健康与 readiness 检查。
- `ai-check.ps1`：AI live/ready 与检索审计巡检。
- `capture-regression.ps1`：抓拍、查询、向量检索与重试队列回归。
- `full-check.ps1`：登录、模拟抓拍、研判与输出联调。
