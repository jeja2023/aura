/* 文件：海康 ISAPI 通用网关（HikvisionIsapiGatewayService.cs） | File: Hikvision ISAPI gateway */
using Aura.Api.Data;
using Aura.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text;

namespace Aura.Api.Services.Hikvision;

/// <summary>将官方 Demo 中任意 ISAPI 调用映射为单次 HTTP 转发（路径受白名单约束）。</summary>
internal sealed class HikvisionIsapiGatewayService
{
    private readonly PgSqlStore _db;
    private readonly AppStore _store;
    private readonly IConfiguration _configuration;
    private readonly HikvisionIsapiClient _client;
    private readonly IOptions<HikvisionIsapiOptions> _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HikvisionIsapiGatewayService> _logger;

    public HikvisionIsapiGatewayService(
        PgSqlStore db,
        AppStore store,
        IConfiguration configuration,
        HikvisionIsapiClient client,
        IOptions<HikvisionIsapiOptions> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HikvisionIsapiGatewayService> logger)
    {
        _db = db;
        _store = store;
        _configuration = configuration;
        _client = client;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<IResult> ExecuteAsync(HikvisionIsapiGatewayReq req, CancellationToken cancellationToken)
    {
        var opt = _options.Value;
        if (!opt.GatewayEnabled)
        {
            return Results.Json(new { code = 40301, msg = "海康 ISAPI 网关已在配置中关闭" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!HikvisionIsapiPathGuard.TryValidate(req.PathAndQuery, opt.GatewayMaxPathLength, out var pathError))
        {
            return Results.BadRequest(new { code = 40004, msg = pathError });
        }

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

        if (!TryParseMethod(req.Method, out var httpMethod))
        {
            return Results.BadRequest(new { code = 40005, msg = "仅支持 GET、PUT、POST、DELETE" });
        }

        byte[]? body = null;
        if (!string.IsNullOrEmpty(req.BodyBase64))
        {
            try
            {
                body = Convert.FromBase64String(req.BodyBase64);
            }
            catch
            {
                return Results.BadRequest(new { code = 40006, msg = "BodyBase64 不是有效的 Base64" });
            }
        }
        else if (!string.IsNullOrEmpty(req.Body))
        {
            body = Encoding.UTF8.GetBytes(req.Body);
        }

        if (body != null && body.Length > opt.GatewayMaxRequestBodyBytes)
        {
            return Results.BadRequest(new { code = 40007, msg = "请求体超过配置上限" });
        }

        var baseUri = HikvisionIsapiBaseUri.Build(resolved.Value.Ip, resolved.Value.Port, opt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.GatewayTimeoutSeconds, 1, 3600));

        if (opt.GatewayAuditLogEnabled)
        {
            var actor = _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "anonymous";
            _logger.LogInformation(
                "海康网关审计。操作者={Actor}, deviceId={DeviceId}, method={Method}, path={Path}",
                actor,
                req.DeviceId,
                req.Method,
                HikvisionIsapiLogFormatting.TruncateForLog(req.PathAndQuery, 500));
        }

        using var gatewayActivity = opt.TelemetryActivitiesEnabled
            ? HikvisionIsapiActivity.StartGateway(req.DeviceId, httpMethod.ToString(), req.PathAndQuery)
            : null;
        _ = gatewayActivity;

        var result = await _client.SendAsync(
            httpMethod,
            baseUri,
            req.PathAndQuery,
            body,
            string.IsNullOrWhiteSpace(req.ContentType) ? null : req.ContentType.Trim(),
            cred.Value.UserName,
            cred.Value.Password,
            timeout,
            opt.SkipSslCertificateValidation,
            req.PreferBinaryResponse,
            Math.Clamp(opt.GatewayMaxResponseBinaryBytes, 1024, 200 * 1024 * 1024),
            Math.Clamp(opt.GatewayMaxResponseTextChars, 1024, 20_000_000),
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            HikvisionIsapiMetrics.GatewayInvocation(false);
            var deviceBodyLog = HikvisionIsapiLogFormatting.TruncateForLog(result.ErrorBody, opt.GatewayDeviceErrorBodyLogMaxChars);
            _logger.LogWarning(
                "网关转发失败。deviceId={DeviceId}, path={Path}, detail={Detail}, deviceBody={DeviceBody}",
                req.DeviceId,
                req.PathAndQuery,
                result.Message,
                deviceBodyLog);

            if (opt.GatewayIncludeDeviceErrorBodyIn502)
            {
                return Results.Json(new
                {
                    code = 50203,
                    msg = "ISAPI 网关转发失败",
                    detail = result.Message,
                    httpStatus = result.HttpStatus,
                    deviceId = req.DeviceId,
                    raw = result.ErrorBody
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Json(new
            {
                code = 50203,
                msg = "ISAPI 网关转发失败",
                detail = result.Message,
                httpStatus = result.HttpStatus,
                deviceId = req.DeviceId
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        HikvisionIsapiMetrics.GatewayInvocation(true);

        var p = result.Data;
        if (p.IsBinary && p.BinaryBody is not null)
        {
            return Results.Ok(new
            {
                code = 0,
                msg = "成功",
                data = new
                {
                    deviceId = req.DeviceId,
                    httpStatus = result.HttpStatus,
                    contentType = p.ContentType,
                    isBinary = true,
                    bodyBase64 = Convert.ToBase64String(p.BinaryBody)
                }
            });
        }

        return Results.Ok(new
        {
            code = 0,
            msg = "成功",
            data = new
            {
                deviceId = req.DeviceId,
                httpStatus = result.HttpStatus,
                contentType = p.ContentType,
                isBinary = false,
                body = p.TextBody
            }
        });
    }

    private static bool TryParseMethod(string method, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HttpMethod? httpMethod)
    {
        httpMethod = null;
        if (string.IsNullOrWhiteSpace(method))
        {
            return false;
        }

        var m = method.Trim().ToUpperInvariant();
        httpMethod = m switch
        {
            "GET" => HttpMethod.Get,
            "PUT" => HttpMethod.Put,
            "POST" => HttpMethod.Post,
            "DELETE" => HttpMethod.Delete,
            _ => null
        };
        return httpMethod is not null;
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
}
