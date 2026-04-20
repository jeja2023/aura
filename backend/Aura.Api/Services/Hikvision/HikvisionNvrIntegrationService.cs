/* 文件：海康 NVR ISAPI 对接服务（HikvisionNvrIntegrationService.cs） | File: Hikvision NVR ISAPI integration */
using System.Diagnostics;
using System.Net.Http;
using Aura.Api.Data;
using Aura.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aura.Api.Services.Hikvision;

/// <summary>码流类型，与官方 Demo <c>Streaming.StreamType</c> 一致：主码流 / 子码流 / 第三路。</summary>
internal enum HikvisionDemoStreamType
{
    Main = 0,
    Sub = 1,
    Other = 2
}

/// <summary>按官方 AppsDemo 路径封装设备信息、通道与抓图。</summary>
internal sealed class HikvisionNvrIntegrationService
{
    private readonly PgSqlStore _db;
    private readonly AppStore _store;
    private readonly IConfiguration _configuration;
    private readonly HikvisionIsapiClient _client;
    private readonly IOptions<HikvisionIsapiOptions> _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HikvisionNvrIntegrationService> _logger;

    public HikvisionNvrIntegrationService(
        PgSqlStore db,
        AppStore store,
        IConfiguration configuration,
        HikvisionIsapiClient client,
        IOptions<HikvisionIsapiOptions> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HikvisionNvrIntegrationService> logger)
    {
        _db = db;
        _store = store;
        _configuration = configuration;
        _client = client;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<IResult> GetDeviceInfoAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        var cred = ResolveCredentials(req.UserName, req.Password);
        if (cred is null)
        {
            return Results.BadRequest(new { code = 40002, msg = "未配置海康 ISAPI 账号密码，请在配置 Hikvision:Isapi 或请求体中提供 UserName/Password" });
        }

        var opt = _options.Value;
        AuditDeviceCall("device_info", req.DeviceId, "/ISAPI/System/deviceInfo");
        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 1, 120));

        var result = await _client.GetStringAsync(
            baseUri,
            "/ISAPI/System/deviceInfo",
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            cancellationToken);

        if (!result.Success)
        {
            HikvisionIsapiMetrics.RecordDeviceApi("device_info", false);
            return DeviceApi502Standard(50201, "读取设备信息失败", req.DeviceId, result.Message, result.HttpStatus, result.ErrorBody);
        }

        HikvisionIsapiMetrics.RecordDeviceApi("device_info", true);
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                name = resolved.Value.Name,
                ip = resolved.Value.Ip,
                port = resolved.Value.Port,
                rawXml = result.Data
            }
        });
    }

    /// <summary>轻量探测设备 <c>/ISAPI/System/deviceInfo</c> 是否可达（成功响应不含 XML 正文，仅长度元数据）。</summary>
    public async Task<IResult> ProbeConnectivityAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        var cred = ResolveCredentials(req.UserName, req.Password);
        if (cred is null)
        {
            return Results.BadRequest(new { code = 40002, msg = "未配置海康 ISAPI 账号密码，请在配置 Hikvision:Isapi 或请求体中提供 UserName/Password" });
        }

        var opt = _options.Value;
        AuditDeviceCall("connectivity", req.DeviceId, "/ISAPI/System/deviceInfo");
        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var probeSec = opt.ConnectivityProbeTimeoutSeconds > 0
            ? opt.ConnectivityProbeTimeoutSeconds
            : opt.RequestTimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(probeSec, 1, 120));

        var sw = Stopwatch.StartNew();
        var result = await _client.GetStringAsync(
            baseUri,
            "/ISAPI/System/deviceInfo",
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            cancellationToken);
        sw.Stop();

        if (!result.Success)
        {
            HikvisionIsapiMetrics.RecordDeviceApi("connectivity", false);
            return DeviceApi502Standard(50206, "设备 ISAPI 不可达或认证失败", req.DeviceId, result.Message, result.HttpStatus, result.ErrorBody);
        }

        HikvisionIsapiMetrics.RecordDeviceApi("connectivity", true);
        var responseChars = result.Data?.Length ?? 0;
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                name = resolved.Value.Name,
                ip = resolved.Value.Ip,
                port = resolved.Value.Port,
                reachable = true,
                latencyMs = sw.ElapsedMilliseconds,
                httpStatus = result.HttpStatus,
                responseChars
            }
        });
    }

    public async Task<IResult> GetVideoInputsChannelsAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(
            req,
            "/ISAPI/System/Video/inputs/channels",
            "模拟通道列表",
            "video_inputs_channels",
            cancellationToken);
    }

    public async Task<IResult> GetInputProxyChannelsAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(
            req,
            "/ISAPI/ContentMgmt/InputProxy/channels",
            "数字通道列表",
            "input_proxy_channels",
            cancellationToken);
    }

    public async Task<IResult> GetInputProxyChannelsStatusAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(
            req,
            "/ISAPI/ContentMgmt/InputProxy/channels/status",
            "数字通道状态",
            "input_proxy_channels_status",
            cancellationToken);
    }

    public async Task<IResult> SnapshotAsync(HikvisionIsapiSnapshotReq req, CancellationToken cancellationToken)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        var cred = ResolveCredentials(req.UserName, req.Password);
        if (cred is null)
        {
            return Results.BadRequest(new { code = 40002, msg = "未配置海康 ISAPI 账号密码" });
        }

        if (req.ChannelIndex < 1 || req.ChannelIndex > 512)
        {
            return Results.BadRequest(new { code = 40003, msg = "通道序号 ChannelIndex 应在 1～512 之间（与官方 Demo 一致）" });
        }

        var opt = _options.Value;
        var streamKind = (HikvisionDemoStreamType)Math.Clamp(req.StreamType, 0, 2);
        var streamingChannelId = BuildStreamingChannelId(req.ChannelIndex, streamKind);
        var path = $"/ISAPI/Streaming/channels/{streamingChannelId}/picture?snapShotImageType={Uri.EscapeDataString(opt.SnapShotImageType)}";

        AuditDeviceCall("snapshot", req.DeviceId, path);
        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 1, 120));

        var result = await _client.GetBytesAsync(
            baseUri,
            path,
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            Math.Clamp(opt.MaxSnapshotBytes, 64 * 1024, 50 * 1024 * 1024),
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            HikvisionIsapiMetrics.RecordDeviceApi("snapshot", false);
            return DeviceApi502Snapshot(
                50202,
                "抓图失败",
                req.DeviceId,
                streamingChannelId,
                result.Message,
                result.HttpStatus,
                result.ErrorBody);
        }

        HikvisionIsapiMetrics.RecordDeviceApi("snapshot", true);
        var b64 = Convert.ToBase64String(result.Data);
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                channelIndex = req.ChannelIndex,
                streamType = streamKind.ToString(),
                streamingChannelId,
                imageBase64 = b64,
                captureTime = DateTimeOffset.Now
            }
        });
    }

    private async Task<IResult> GetRawPathAsync(
        HikvisionIsapiDeviceOpReq req,
        string path,
        string label,
        string metricOperation,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        var cred = ResolveCredentials(req.UserName, req.Password);
        if (cred is null)
        {
            return Results.BadRequest(new { code = 40002, msg = "未配置海康 ISAPI 账号密码" });
        }

        var opt = _options.Value;
        AuditDeviceCall(metricOperation, req.DeviceId, path);
        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 1, 120));

        var result = await _client.GetStringAsync(
            baseUri,
            path,
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            cancellationToken);

        if (!result.Success)
        {
            HikvisionIsapiMetrics.RecordDeviceApi(metricOperation, false);
            var deviceBodyLog = HikvisionIsapiLogFormatting.TruncateForLog(result.ErrorBody, opt.GatewayDeviceErrorBodyLogMaxChars);
            _logger.LogWarning(
                "读取{Label}失败。deviceId={DeviceId}, detail={Detail}, deviceBody={DeviceBody}",
                label,
                req.DeviceId,
                result.Message,
                deviceBodyLog);
            return DeviceApi502Standard(50201, $"读取{label}失败", req.DeviceId, result.Message, result.HttpStatus, result.ErrorBody);
        }

        HikvisionIsapiMetrics.RecordDeviceApi(metricOperation, true);
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                label,
                rawXml = result.Data
            }
        });
    }

    private async Task<(long DeviceId, string Name, string Ip, int Port)?> ResolveEndpointAsync(long deviceId)
    {
        var row = await _db.GetDeviceByIdAsync(deviceId);
        if (row is not null)
        {
            return (row.DeviceId, row.Name, row.Ip, row.Port);
        }

        if (_configuration.GetValue("Aura:AllowInMemoryDataFallback", false))
        {
            var mem = _store.Devices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (mem is not null)
            {
                return (mem.DeviceId, mem.Name, mem.Ip, mem.Port);
            }
        }

        return null;
    }

    private (string UserName, string Password)? ResolveCredentials(string? userName, string? password)
    {
        var opt = _options.Value;
        var u = string.IsNullOrWhiteSpace(userName) ? opt.DefaultUserName : userName;
        var p = string.IsNullOrWhiteSpace(password) ? opt.DefaultPassword : password;
        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p))
        {
            return null;
        }

        return (u.Trim(), p!);
    }

    public async Task<IResult> RequestKeyFrameAsync(HikvisionIsapiKeyFrameReq req, CancellationToken cancellationToken)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        var cred = ResolveCredentials(req.UserName, req.Password);
        if (cred is null)
        {
            return Results.BadRequest(new { code = 40002, msg = "未配置海康 ISAPI 账号密码" });
        }

        if (string.IsNullOrWhiteSpace(req.StreamingChannelId) || req.StreamingChannelId.Length > 16)
        {
            return Results.BadRequest(new { code = 40008, msg = "StreamingChannelId 无效（示例：101 表示第 1 路主码流）" });
        }

        var opt = _options.Value;
        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 1, 120));
        var path = $"/ISAPI/Streaming/channels/{Uri.EscapeDataString(req.StreamingChannelId.Trim())}/requestKeyFrame";

        AuditDeviceCall("request_key_frame", req.DeviceId, path);
        var result = await _client.SendAsync(
            HttpMethod.Put,
            baseUri,
            path,
            null,
            null,
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            preferBinaryResponse: false,
            maxBinaryBytes: 1024,
            maxTextChars: 64 * 1024,
            cancellationToken);

        if (!result.Success)
        {
            HikvisionIsapiMetrics.RecordDeviceApi("request_key_frame", false);
            return DeviceApi502Standard(50204, "请求关键帧失败", req.DeviceId, result.Message, result.HttpStatus, result.ErrorBody);
        }

        HikvisionIsapiMetrics.RecordDeviceApi("request_key_frame", true);
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                streamingChannelId = req.StreamingChannelId.Trim(),
                httpStatus = result.HttpStatus,
                body = result.Data?.TextBody
            }
        });
    }

    public async Task<IResult> GetSystemCapabilitiesJsonAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(req, "/ISAPI/System/capabilities?format=json", "系统能力（JSON）", "system_capabilities_json", cancellationToken);
    }

    public async Task<IResult> GetEventCapabilitiesAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(req, "/ISAPI/Event/capabilities", "事件能力", "event_capabilities", cancellationToken);
    }

    public async Task<IResult> GetZeroVideoChannelsAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(req, "/ISAPI/ContentMgmt/ZeroVideo/channels", "零通道列表", "zero_video_channels", cancellationToken);
    }

    public async Task<IResult> GetTrafficCapabilitiesAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(req, "/ISAPI/Traffic/capabilities", "交通能力", "traffic_capabilities", cancellationToken);
    }

    public async Task<IResult> GetItcCapabilityAsync(HikvisionIsapiDeviceOpReq req, CancellationToken cancellationToken)
    {
        return await GetRawPathAsync(req, "/ISAPI/ITC/capability", "智能交通能力", "itc_capability", cancellationToken);
    }

    /// <summary>
    /// 返回海康典型 RTSP 路径提示（不含账号口令）；实况/回放仍由流媒体子系统或播放器直连设备。
    /// </summary>
    public Task<IResult> GetMediaStreamHintAsync(MediaStreamHintReq req, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return GetMediaStreamHintCoreAsync(req);
    }

    private async Task<IResult> GetMediaStreamHintCoreAsync(MediaStreamHintReq req)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        if (req.ChannelIndex < 1 || req.ChannelIndex > 512)
        {
            return Results.BadRequest(new { code = 40003, msg = "通道序号 ChannelIndex 应在 1～512 之间" });
        }

        var streamKind = (HikvisionDemoStreamType)Math.Clamp(req.StreamType, 0, 2);
        var streamingChannelId = BuildStreamingChannelId(req.ChannelIndex, streamKind);
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                host = resolved.Value.Ip,
                httpPort = resolved.Value.Port,
                rtspPort = 554,
                streamingChannelId,
                rtspPath = $"/Streaming/Channels/{streamingChannelId}",
                rtspUrlTemplate = "rtsp://{host}:{rtspPort}{rtspPath}",
                note = "请使用 VLC/流媒体服务等以设备凭据连接 RTSP；本平台 API 不转发音视频码流，录像回放需在媒体子系统对接设备回放或 NVR VOD。"
            }
        });
    }

    private static string BuildStreamingChannelId(int channelIndex, HikvisionDemoStreamType streamKind)
    {
        var streamSuffix = streamKind switch
        {
            HikvisionDemoStreamType.Main => "01",
            HikvisionDemoStreamType.Sub => "02",
            HikvisionDemoStreamType.Other => "03",
            _ => "01"
        };

        return $"{channelIndex}{streamSuffix}";
    }

    /// <summary>POST <c>/ISAPI/SDT/pictureUpload</c>，表单字段 <c>imageFile</c>，与官方 Demo 一致。</summary>
    public async Task<IResult> SdtPictureUploadAsync(HikvisionIsapiSdtPictureUploadReq req, CancellationToken cancellationToken)
    {
        var resolved = await ResolveEndpointAsync(req.DeviceId);
        if (resolved is null)
        {
            return Results.NotFound(new { code = 40401, msg = "设备不存在或未在库中注册" });
        }

        var cred = ResolveCredentials(req.UserName, req.Password);
        if (cred is null)
        {
            return Results.BadRequest(new { code = 40002, msg = "未配置海康 ISAPI 账号密码" });
        }

        if (string.IsNullOrWhiteSpace(req.ImageBase64))
        {
            return Results.BadRequest(new { code = 40009, msg = "ImageBase64 不能为空" });
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(req.ImageBase64);
        }
        catch
        {
            return Results.BadRequest(new { code = 40006, msg = "ImageBase64 不是有效的 Base64" });
        }

        var opt = _options.Value;
        var maxBytes = Math.Clamp(opt.MaxSdtPictureUploadBytes, 1024, opt.GatewayMaxRequestBodyBytes);
        if (bytes.Length > maxBytes)
        {
            return Results.BadRequest(new { code = 40010, msg = $"图片数据超过配置上限（{maxBytes} 字节）" });
        }

        var rawName = string.IsNullOrWhiteSpace(req.FileName) ? "upload.jpg" : req.FileName.Trim();
        if (rawName.IndexOfAny(['/', '\\']) >= 0
            || rawName.Contains("..", StringComparison.Ordinal)
            || rawName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Results.BadRequest(new { code = 40011, msg = "FileName 无效（仅允许不含路径的合法文件名）" });
        }

        var safeName = Path.GetFileName(rawName);
        if (string.IsNullOrEmpty(safeName))
        {
            return Results.BadRequest(new { code = 40011, msg = "FileName 无效（仅允许不含路径的合法文件名）" });
        }

        var partType = string.IsNullOrWhiteSpace(req.PartContentType) ? "image/jpeg" : req.PartContentType.Trim();

        AuditDeviceCall("sdt_picture_upload", req.DeviceId, "/ISAPI/SDT/pictureUpload");
        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 1, 120));

        var result = await _client.SendMultipartPostAsync(
            baseUri,
            "/ISAPI/SDT/pictureUpload",
            "imageFile",
            safeName,
            partType,
            bytes,
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            Math.Clamp(opt.GatewayMaxResponseTextChars, 1024, 20_000_000),
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            HikvisionIsapiMetrics.RecordDeviceApi("sdt_picture_upload", false);
            var deviceBodyLog = HikvisionIsapiLogFormatting.TruncateForLog(result.ErrorBody, opt.GatewayDeviceErrorBodyLogMaxChars);
            _logger.LogWarning(
                "SDT 图片上传失败。deviceId={DeviceId}, detail={Detail}, deviceBody={DeviceBody}",
                req.DeviceId,
                result.Message,
                deviceBodyLog);
            return DeviceApi502Standard(50205, "SDT 图片上传失败", req.DeviceId, result.Message, result.HttpStatus, result.ErrorBody);
        }

        HikvisionIsapiMetrics.RecordDeviceApi("sdt_picture_upload", true);
        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                httpStatus = result.HttpStatus,
                contentType = result.Data.ContentType,
                body = result.Data.TextBody
            }
        });
    }

    private void AuditDeviceCall(string operation, long deviceId, string? pathOrDetail)
    {
        var opt = _options.Value;
        if (!opt.DeviceApiAuditLogEnabled)
        {
            return;
        }

        var actor = _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "anonymous";
        var detail = HikvisionIsapiLogFormatting.TruncateForLog(pathOrDetail, 500);
        _logger.LogInformation(
            "海康设备接口审计。操作={Operation}, 操作者={Actor}, deviceId={DeviceId}, 详情={Detail}",
            operation,
            actor,
            deviceId,
            detail);
    }

    private IResult DeviceApi502Standard(int code, string msg, long deviceId, string? detail, int? httpStatus, string? rawBody)
    {
        if (_options.Value.DeviceApiIncludeErrorBodyIn502)
        {
            return Results.Json(
                new { code, msg, detail, httpStatus, deviceId, raw = rawBody },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Json(
            new { code, msg, detail, httpStatus, deviceId },
            statusCode: StatusCodes.Status502BadGateway);
    }

    private IResult DeviceApi502Snapshot(
        int code,
        string msg,
        long deviceId,
        string streamingChannelId,
        string? detail,
        int? httpStatus,
        string? rawBody)
    {
        if (_options.Value.DeviceApiIncludeErrorBodyIn502)
        {
            return Results.Json(
                new
                {
                    code,
                    msg,
                    detail,
                    httpStatus,
                    deviceId,
                    streamingChannelId,
                    raw = rawBody
                },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Json(
            new
            {
                code,
                msg,
                detail,
                httpStatus,
                deviceId,
                streamingChannelId
            },
            statusCode: StatusCodes.Status502BadGateway);
    }
}
