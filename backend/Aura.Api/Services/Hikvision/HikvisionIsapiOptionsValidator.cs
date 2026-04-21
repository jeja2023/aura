/* 文件：海康 ISAPI 配置校验（HikvisionIsapiOptionsValidator.cs） | File: Hikvision ISAPI options validator */
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aura.Api.Services.Hikvision;

/// <summary>启动时校验 <see cref="HikvisionIsapiOptions"/>，避免运行时发现非法组合。</summary>
internal sealed class HikvisionIsapiOptionsValidator : IValidateOptions<HikvisionIsapiOptions>
{
    private const int ProductionMinRequestsPerMinute = 1;
    private readonly IHostEnvironment _environment;

    public HikvisionIsapiOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, HikvisionIsapiOptions options)
    {
        var errors = new List<string>();

        if (options.RequestTimeoutSeconds is < 1 or > 600)
        {
            errors.Add("RequestTimeoutSeconds 应在 1～600 之间。");
        }

        if (options.GatewayTimeoutSeconds is < 1 or > 3600)
        {
            errors.Add("GatewayTimeoutSeconds 应在 1～3600 之间。");
        }

        if (string.IsNullOrWhiteSpace(options.SnapShotImageType))
        {
            errors.Add("SnapShotImageType 不能为空。");
        }

        if (options.MaxSnapshotBytes is < 4096 or > 200 * 1024 * 1024)
        {
            errors.Add("MaxSnapshotBytes 超出合理范围。");
        }

        if (options.MaxSdtPictureUploadBytes is < 1024 or > 200 * 1024 * 1024)
        {
            errors.Add("MaxSdtPictureUploadBytes 超出合理范围。");
        }

        if (options.GatewayMaxPathLength is < 64 or > 8192)
        {
            errors.Add("GatewayMaxPathLength 应在 64～8192 之间。");
        }

        if (options.GatewayMaxRequestBodyBytes is < 1024 or > 200 * 1024 * 1024)
        {
            errors.Add("GatewayMaxRequestBodyBytes 超出合理范围。");
        }

        if (options.GatewayMaxResponseTextChars is < 1024 or > 50_000_000)
        {
            errors.Add("GatewayMaxResponseTextChars 超出合理范围。");
        }

        if (options.GatewayMaxResponseBinaryBytes is < 1024 or > 500 * 1024 * 1024)
        {
            errors.Add("GatewayMaxResponseBinaryBytes 超出合理范围。");
        }

        if (options.GatewayMaxRequestsPerMinute is < 0 or > 100_000)
        {
            errors.Add("GatewayMaxRequestsPerMinute 应在 0～100000 之间（0 表示不限流）。");
        }

        if (options.DeviceApiMaxRequestsPerMinute is < 0 or > 100_000)
        {
            errors.Add("DeviceApiMaxRequestsPerMinute 应在 0～100000 之间（0 表示不限流）。");
        }

        if (_environment.IsProduction() && options.GatewayEnabled && options.GatewayMaxRequestsPerMinute < ProductionMinRequestsPerMinute)
        {
            errors.Add($"生产环境启用 Gateway 时，GatewayMaxRequestsPerMinute 必须大于等于 {ProductionMinRequestsPerMinute}。");
        }

        if (_environment.IsProduction() && options.DeviceApiMaxRequestsPerMinute < ProductionMinRequestsPerMinute)
        {
            errors.Add($"生产环境下 DeviceApiMaxRequestsPerMinute 必须大于等于 {ProductionMinRequestsPerMinute}。");
        }

        if (options.ConnectivityProbeTimeoutSeconds is < 0 or > 600)
        {
            errors.Add("ConnectivityProbeTimeoutSeconds 应在 0～600 之间（0 表示使用 RequestTimeoutSeconds）。");
        }

        if (options.GatewayDeviceErrorBodyLogMaxChars is < 0 or > 100_000)
        {
            errors.Add("GatewayDeviceErrorBodyLogMaxChars 超出合理范围。");
        }

        if (!_environment.IsDevelopment()
            && options.SkipSslCertificateValidation
            && !options.AllowInsecureDeviceTls)
        {
            errors.Add("生产环境启用 SkipSslCertificateValidation 时必须同时将 AllowInsecureDeviceTls 设为 true（显式承担风险）。");
        }

        var alert = options.AlertStream;
        if (alert.ReconnectSeconds is < 1 or > 600)
        {
            errors.Add("AlertStream.ReconnectSeconds 应在 1～600 之间。");
        }

        if (alert.SupervisorRefreshSeconds is < 5 or > 3600)
        {
            errors.Add("AlertStream.SupervisorRefreshSeconds 应在 5～3600 之间。");
        }

        if (alert.MaxBufferBytes is < 65536 or > 512 * 1024 * 1024)
        {
            errors.Add("AlertStream.MaxBufferBytes 超出合理范围。");
        }

        if (alert.MaxImageBytes is < 1024 or > 200 * 1024 * 1024)
        {
            errors.Add("AlertStream.MaxImageBytes 超出合理范围。");
        }

        if (alert.DedupWindowSeconds is < 0 or > 600)
        {
            errors.Add("AlertStream.DedupWindowSeconds 应在 0～600 之间（0 表示不去重）。");
        }

        if (!string.IsNullOrWhiteSpace(alert.CameraChannelFallbackStrategy))
        {
            var s = alert.CameraChannelFallbackStrategy.Trim().ToLowerInvariant();
            if (s is not ("first" or "latest"))
            {
                errors.Add("AlertStream.CameraChannelFallbackStrategy 仅支持 first 或 latest。");
            }
        }

        if (alert.XmlPreviewMaxChars is < 256 or > 500_000)
        {
            errors.Add("AlertStream.XmlPreviewMaxChars 应在 256～500000 之间。");
        }

        if (!string.IsNullOrWhiteSpace(alert.PathAndQuery) && !alert.PathAndQuery.TrimStart().StartsWith('/'))
        {
            errors.Add("AlertStream.PathAndQuery 须以 / 开头。");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
