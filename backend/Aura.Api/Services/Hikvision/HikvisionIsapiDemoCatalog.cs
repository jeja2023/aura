/* 文件：海康 AppsDemo ISAPI 路由索引（HikvisionIsapiDemoCatalog.cs） | File: Hikvision demo ISAPI route catalog */
namespace Aura.Api.Services.Hikvision;

/// <summary>与 third-party/C#AppsDemo_ISAPI 各子工程中出现的路径对齐，供联调查阅（非设备实时能力探测）。</summary>
internal static class HikvisionIsapiDemoCatalog
{
    public static object Build()
    {
        return new
        {
            code = 0,
            msg = "官方 Demo 工程与典型 ISAPI 路径索引",
            data = new
            {
                solution = "AppsDemo_ISAPI.sln",
                modules = new object[]
                {
                    new { name = "ISAPISystemManagement", examples = new[] { "/ISAPI/System/deviceInfo", "/ISAPI/System/capabilities?format=json", "/ISAPI/Streaming/channels/{id}/picture", "/ISAPI/Streaming/channels/{id}/requestKeyFrame", "/ISAPI/System/Video/inputs/channels", "/ISAPI/PTZCtrl/channels/{id}/presets" } },
                    new { name = "ISAPIDeviceTree", examples = new[] { "/ISAPI/ContentMgmt/InputProxy/channels", "/ISAPI/ContentMgmt/InputProxy/channels/status", "/ISAPI/System/Video/inputs/channels", "/ISAPI/ContentMgmt/ZeroVideo/channels" } },
                    new { name = "ISAPIAlarm", examples = new[] { "/ISAPI/Event/notification/httpHosts", "/ISAPI/Event/notification/httpHosts/capabilities", "/ISAPI/Event/notification/subscribeEventCap", "/ISAPI/Event/notification/subscribeEvent/{id}", "/ISAPI/Event/notification/alertStream", "/ISAPI/Traffic/MNPR/channels/{id}" } },
                    new { name = "ISAPIFaceSnap", examples = new[] { "/ISAPI/Intelligent/channels/{id}/capabilities", "/ISAPI/Intelligent/channels/{id}/AlgParam" } },
                    new { name = "ISAPIFaceContrast", examples = new[] { "/ISAPI/Intelligent/FDLib/capabilities", "/ISAPI/Event/capabilities", "/ISAPI/Intelligent/channels/{id}/faceContrast/capabilities" } },
                    new { name = "ISAPIFaceLib", examples = new[] { "/ISAPI/Intelligent/FDLib", "/ISAPI/Intelligent/FDLib/FDSearch", "/ISAPI/SDT/pictureUpload", "/ISAPI/SDT/Management/Server/capabilities", "/ISAPI/Intelligent/uploadStorageCloud?format=json" } },
                    new { name = "ISAPIANPR", examples = new[] { "/ISAPI/ITC/capability", "/ISAPI/Traffic/channels/{id}/CurVehicleDetectMode", "/ISAPI/Traffic/channels/{id}/licensePlateAuditData", "/ISAPI/ITC/Entrance/barrierGateCtrl" } },
                    new { name = "ISAPIPerimeterPrecaution", examples = new[] { "/ISAPI/Security/adminAccesses", "/ISAPI/Smart/capabilities", "/ISAPI/Smart/LineDetection/{id}", "/ISAPI/Smart/FieldDetection/{id}", "/ISAPI/Smart/regionEntrance/{id}", "/ISAPI/Smart/regionExiting/{id}" } },
                    new { name = "ISAPIMixedTargetDetection", examples = new[] { "/ISAPI/Intelligent/channels/{id}/mixedTargetDetection/capturePicture?format=json", "/ISAPI/Intelligent/channels/{id}/mixedTargetDetection/algParam?format=json" } },
                    new { name = "ISAPIThermometry", examples = new[] { "/ISAPI/Thermal/channels/{id}/thermometry/jpegPicWithAppendData?format=json", "/ISAPI/Streaming/channels/1/metadata/subscribeType?format=json" } },
                    new { name = "ISAPIAIOpenPlatform", examples = new[] { "/ISAPI/Intelligent/AIOpenPlatform/videoTask?format=json" } },
                    new { name = "CommonBase.HttpClient", notes = new[] { "Digest 认证", "HttpRequest GET/PUT", "StartHttpLongLink 订阅流" } }
                },
                credentials = new
                {
                    note = "仓库内 appsettings 不应写入真实设备密码；可用环境变量 Hikvision__Isapi__DefaultUserName / Hikvision__Isapi__DefaultPassword，或在配置中填写 DefaultPasswordEnvironmentVariable 指向自定义环境变量名。",
                    demoSamplePasswords = "官方 C# Demo 的 App.config 无内置账号密码；设备口令由登录窗体或运行时输入，第三方目录内未见硬编码真实口令。"
                },
                longRunning = new
                {
                    alertStream = "/ISAPI/Event/notification/alertStream 等为设备长连接/订阅流，对应 Demo 中 StartHttpLongLink；服务端若需对浏览器推送，应单独做后台任务或 SSE，本批未纳入同步 HTTP 封装。",
                    eventSubscriptionService = "P2：设备事件订阅/长链建议独立「设备事件接入」服务（或后台 Worker）：维护 Digest 长连接、解析 alertStream、落库/去重；与 Aura.Api 同步 HTTP 封装进程分离，经消息队列或内部 API 向业务投递；浏览器侧可用现有 SignalR 二次分发，须单独做订阅鉴权与背压。",
                    rtspAndNative = "RTSP 预览、FFmpeg、TINYXMLTRANS 等属设备侧或桌面端工程，仍通过设备/客户端直连；网关仅覆盖已文档化的单次 ISAPI HTTP。",
                    liveAndPlayback = "P2：实况播放与录像回放属流媒体/回放子系统（取流 URL 与能力探测可经 ISAPI，转封装、分发、时移、多客户端播控与 ISAPI 命令式封装不同层），须单独规划进程与端口策略，不宜并入当前 ISAPI 网关宿主。"
                },
                faceLibUpload = new
                {
                    demoAligned = "POST /api/device/hikvision/sdt/picture-upload（multipart 字段 imageFile，与 CommonMethod.UploadPic 一致）",
                    gatewayEquivalent = "亦可 POST /api/device/hikvision/gateway，Method=POST，PathAndQuery=/ISAPI/SDT/pictureUpload，BodyBase64 为原始字节、Content-Type 为 multipart 完整体（需自行构造 boundary）"
                },
                enterprise = new
                {
                    optionsValidation = "Hikvision:Isapi 在启动时 ValidateOnStart；生产环境若 SkipSslCertificateValidation=true 须同时 AllowInsecureDeviceTls=true。",
                    metrics = "Prometheus：aura_hikvision_isapi_outbound_duration_seconds、aura_hikvision_gateway_invocations_total、aura_hikvision_device_api_calls_total（抓取 /metrics）。",
                    gatewayAudit = "GatewayAuditLogEnabled 为 true 时记录操作者（JWT 身份名）、deviceId、方法、路径；从不记录密码。",
                    deviceApiAudit = "DeviceApiAuditLogEnabled 为 true 时记录封装接口（楼栋管理员）调用：操作名、操作者、deviceId、路径摘要。",
                    deviceApi502 = "DeviceApiIncludeErrorBodyIn502 为 false 时封装接口 502 响应不含 raw，与网关 GatewayIncludeDeviceErrorBodyIn502 独立配置。",
                    connectivity = "POST /api/device/hikvision/connectivity：轻量探测 /ISAPI/System/deviceInfo，成功返回 latencyMs、responseChars，不含完整 XML；ConnectivityProbeTimeoutSeconds=0 时用 RequestTimeoutSeconds。",
                    gatewayRateLimit = "GatewayMaxRequestsPerMinute>0 时对 /gateway 按用户或客户端 IP 固定窗口限流；0 表示不限。",
                    deviceApiRateLimit = "DeviceApiMaxRequestsPerMinute>0 时对楼栋管理员海康封装 POST（含 analyze-response、connectivity 等）限流，与网关策略独立。",
                    kestrelBody = "Kestrel:Limits:MaxRequestBodySize 与 GatewayMaxRequestBodyBytes 对齐，避免全局限制与业务校验不一致。",
                    tracing = "启用 Ops:Telemetry:EnableTracing 且配置 OTLP 后，采集 ActivitySource Aura.HikvisionIsapi。",
                    openApi = "海康路由组已标注 OpenAPI 标签「海康ISAPI」。"
                },
                auraEndpoints = new
                {
                    device = "/api/device/hikvision/*",
                    gateway = "POST /api/device/hikvision/gateway（仅超级管理员）",
                    media = "/api/media/*（取流规划与 RTSP 路径提示，不代理码流）",
                    alertStreamWorker = "Hikvision:Isapi:AlertStream.Enabled=true 时由后台 HostedService 拉取 alertStream，事件摘要经 SignalR hikvision.alertStream 推送；GET /api/device/hikvision/alert-stream-status 查看进程内连接阶段与最近错误（非持久化）。"
                }
            }
        };
    }
}
