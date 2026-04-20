/* 文件：海康 ISAPI 选项（HikvisionIsapiOptions.cs） | File: Hikvision ISAPI options */
namespace Aura.Api.Services.Hikvision;

/// <summary>与官方 AppsDemo 中 HttpClient 默认超时、抓图参数对齐的可配置项。</summary>
internal sealed class HikvisionIsapiOptions
{
    public const string SectionName = "Hikvision:Isapi";

    /// <summary>单次请求超时（秒），Demo 中默认为 5000ms。</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>抓图查询参数 snapShotImageType，如 JPEG。</summary>
    public string SnapShotImageType { get; set; } = "JPEG";

    /// <summary>是否使用 HTTPS 访问设备 Web 服务端口。</summary>
    public bool UseHttps { get; set; }

    /// <summary>跳过服务端证书校验（仅建议联调网段使用，生产应关闭）。</summary>
    public bool SkipSslCertificateValidation { get; set; }

    /// <summary>非开发环境若必须跳过设备 TLS 校验，须显式设为 true，否则启动校验失败。</summary>
    public bool AllowInsecureDeviceTls { get; set; }

    /// <summary>默认登录名（可被接口请求体覆盖）。</summary>
    public string DefaultUserName { get; set; } = "";

    /// <summary>默认密码（可被接口请求体覆盖）。</summary>
    public string DefaultPassword { get; set; } = "";

    /// <summary>当 <see cref="DefaultUserName"/> 为空时，从该环境变量名读取用户名（便于与密钥平台变量名对齐）。</summary>
    public string DefaultUserNameEnvironmentVariable { get; set; } = "";

    /// <summary>当 <see cref="DefaultPassword"/> 为空时，从该环境变量名读取密码（避免在配置文件中写明文）。</summary>
    public string DefaultPasswordEnvironmentVariable { get; set; } = "";

    /// <summary>抓图响应最大字节数，防止异常大包占用内存。</summary>
    public int MaxSnapshotBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>SDT 图片上传（人脸库流程）单文件最大字节数，与 Demo 中 imageFile 大小上限对齐用途。</summary>
    public int MaxSdtPictureUploadBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>是否启用通用 ISAPI 网关（超级管理员）。</summary>
    public bool GatewayEnabled { get; set; } = true;

    /// <summary>网关单次请求超时（秒），告警长连接等可调大。</summary>
    public int GatewayTimeoutSeconds { get; set; } = 120;

    /// <summary>网关允许的路径最大长度。</summary>
    public int GatewayMaxPathLength { get; set; } = 2048;

    /// <summary>网关请求体最大字节数（含 Base64 解码后）。</summary>
    public int GatewayMaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>网关文本响应最大字符数（近似）。</summary>
    public int GatewayMaxResponseTextChars { get; set; } = 5_000_000;

    /// <summary>网关二进制响应最大字节数。</summary>
    public int GatewayMaxResponseBinaryBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>是否记录网关调用的审计日志（操作者、设备、路径，不含密码）。</summary>
    public bool GatewayAuditLogEnabled { get; set; } = true;

    /// <summary>502 响应是否包含设备返回体（raw），生产可关闭以降低信息泄露风险。</summary>
    public bool GatewayIncludeDeviceErrorBodyIn502 { get; set; } = true;

    /// <summary>网关失败时写入日志的设备响应正文最大字符数（防止日志爆量）。</summary>
    public int GatewayDeviceErrorBodyLogMaxChars { get; set; } = 512;

    /// <summary>网关按登录用户或客户端 IP 的每分钟最大请求数，0 表示不限流。</summary>
    public int GatewayMaxRequestsPerMinute { get; set; }

    /// <summary>是否发出海康 ISAPI 相关的分布式追踪 Activity（与 OpenTelemetry 兼容）。</summary>
    public bool TelemetryActivitiesEnabled { get; set; } = true;

    /// <summary>楼栋管理员封装接口在 502 时是否向客户端返回设备错误正文（raw），与网关策略可分别配置。</summary>
    public bool DeviceApiIncludeErrorBodyIn502 { get; set; } = true;

    /// <summary>是否记录封装接口调用审计（操作者、deviceId、操作名与路径摘要，不含密码）。</summary>
    public bool DeviceApiAuditLogEnabled { get; set; } = true;

    /// <summary>楼栋管理员封装接口（不含网关）按用户或 IP 的每分钟最大请求数，0 表示不限流。</summary>
    public int DeviceApiMaxRequestsPerMinute { get; set; }

    /// <summary>连通性探测专用超时（秒），0 表示使用 <see cref="RequestTimeoutSeconds"/>。</summary>
    public int ConnectivityProbeTimeoutSeconds { get; set; }

    /// <summary>设备 <c>alertStream</c> 长连接订阅（独立后台任务，与同步 ISAPI 封装解耦）。</summary>
    public HikvisionAlertStreamOptions AlertStream { get; set; } = new();
}

/// <summary>海康事件告警长连接（<c>/ISAPI/Event/notification/alertStream</c>）Worker 配置。</summary>
public sealed class HikvisionAlertStreamOptions
{
    /// <summary>是否启用后台长连接；启用后需配置有效默认凭据。</summary>
    public bool Enabled { get; set; }

    /// <summary>仅订阅这些设备；为空则自动选择协议含 ISAPI 且品牌含海康/HIK 的登记设备。</summary>
    public long[] DeviceIds { get; set; } = [];

    /// <summary>长连接路径（含查询串）。</summary>
    public string PathAndQuery { get; set; } = "/ISAPI/Event/notification/alertStream";

    /// <summary>断线后重连间隔（秒）。</summary>
    public int ReconnectSeconds { get; set; } = 10;

    /// <summary>监管循环刷新设备列表间隔（秒）。</summary>
    public int SupervisorRefreshSeconds { get; set; } = 30;

    /// <summary>multipart 重组缓冲区上限（字节），超出则丢弃缓冲并记警告。</summary>
    public int MaxBufferBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>是否经 SignalR 向楼栋管理员/超级管理员组推送解析摘要。</summary>
    public bool PushSignalR { get; set; } = true;

    /// <summary>推送与日志中 XML 预览最大字符数。</summary>
    public int XmlPreviewMaxChars { get; set; } = 4096;

    /// <summary>是否将心跳类 JSON 以 Information 级别写入日志（默认仅 Debug）。</summary>
    public bool LogHeartbeats { get; set; }
}
