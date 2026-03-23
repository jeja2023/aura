# 第三方脚本离线包（vendor）

本目录存放前端页面直接引用的 **UMD/浏览器压缩脚本**，与业务代码一并纳入版本管理，**运行时不再请求公共 CDN**。

| 文件名 | 说明 | 当前版本参考 |
|--------|------|----------------|
| `three.min.js` | Three.js | 0.160.0 |
| `signalr.min.js` | @microsoft/signalr 浏览器包 | 8.0.7 |
| `echarts.min.js` | Apache ECharts | 5.6.0 |

## 升级或补全文件

若需升级版本，在可访问外网的开发机上从对应 npm 包的发布文件拷贝同名 `*.min.js` 覆盖本目录即可；或通过包管理器解压 `node_modules` 内上述路径文件到此处。

加载逻辑见：

- `../scene-vendor-loader.js`（Three + SignalR + `scene.js`）
- `../signalr-vendor-loader.js`（SignalR + 各页业务脚本）
- `../echarts-vendor-loader.js`（ECharts + `stats.js`）
