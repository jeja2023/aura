/* 文件：海康 ISAPI 配置校验（HikvisionIsapiOptionsValidator.cs） | File: Hikvision ISAPI options validator */
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aura.Api.Services.Hikvision;

/// <summary>启动时校验 <see cref="HikvisionIsapiOptions"/>，避免运行时发现非法组合。</summary>
internal sealed class HikvisionIsapiOptionsValidator : IValidateOptions<HikvisionIsapiOptions>
{
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

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
