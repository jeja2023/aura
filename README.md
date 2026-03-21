# 寓瞳系统（第五阶段完成态）

本仓库已按《开发计划.md》《开发规范.md》完成第一至第五阶段开发，覆盖接入网关、AI 特征链路、空间引擎、业务研判、3D/2D 态势、统计导出与外联输出。

## 项目状态

- 当前版本：`0.0.5`
- 版本序列：`0.0.1` -> `0.0.2` -> `0.0.3` -> `0.0.4` -> `0.0.5`
- 阶段状态：第一至第五阶段均已验收通过
- 交付结论：计划项已全部完成并在 `开发计划.md` 归档勾选
- 工程状态：后端可构建、前端页面可访问、核心链路可联调
- 运维状态：已提供回归脚本、联调压测脚本、部署与上线检查文档

## 目录结构

- `backend/Aura.Api`：.NET 10 WebAPI 中枢服务
- `ai`：Python FastAPI AI 服务（特征提取/检索）
- `database/schema.sql`：MySQL 表结构
- `frontend`：Vanilla JS 前端页面
- `抓拍链路端到端测试清单.md`：抓拍链路测试清单
- `抓拍链路回归脚本.ps1`：抓拍链路回归脚本
- `全系统联调与压测脚本.ps1`：全系统联调与压测脚本
- `部署文档与运维手册.md`：部署与运维手册
- `最终交付清单.md`：最终交付范围清单
- `上线检查清单.md`：上线前检查清单

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

导入 `database/schema.sql` 到 MySQL 8.0+。

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

### 4) 打开前端

默认可直接通过后端同域名访问：`https://localhost:5001/`  
（后端已挂载项目根目录 `frontend` 为静态资源目录）

## 默认测试账号

- 用户名：`admin`
- 密码：`admin123`

> 生产环境请务必禁用或改密默认账号，并替换 `appsettings.Production.json` 中全部占位密钥。

## 关键页面入口

- 首页看板：`frontend/index/index.html`
- 三维态势：`frontend/scene/scene.html`
- 统计驾驶舱：`frontend/stats/stats.html`
- 报表导出：`frontend/export/export.html`
- 以图搜轨：`frontend/search/search.html`

## 关键接口（示例）

- 登录：`POST /api/auth/login`
- 抓拍接入：`POST /api/capture/push|sdk|onvif`
- 空间碰撞：`POST /api/space/collision/check`
- 研判执行：`POST /api/judge/run/daily`
- 统计驾驶舱：`GET /api/stats/dashboard`
- 导出：`GET /api/export/{type}?dataset=capture|alert|judge`
- 外联输出：`GET /api/output/events`、`GET /api/output/persons`

## 回归与压测

- 抓拍链路回归：`powershell -ExecutionPolicy Bypass -File "e:\Aura\抓拍链路回归脚本.ps1"`
- 全系统联调压测：`powershell -ExecutionPolicy Bypass -File "e:\Aura\全系统联调与压测脚本.ps1"`

## 部署建议

- 参考 `backend/Aura.Api/appsettings.Production.json` 填充生产配置
- 参考 `部署文档与运维手册.md` 与 `上线检查清单.md` 执行上线流程
