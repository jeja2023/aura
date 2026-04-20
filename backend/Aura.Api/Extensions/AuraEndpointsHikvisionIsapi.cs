/* 文件：海康 NVR ISAPI 对接端点 | File: Hikvision NVR ISAPI endpoints */
using Aura.Api.Models;
using Aura.Api.Services.Hikvision;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsHikvisionIsapi
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        _ = ctx;
        var g = app.MapGroup("/api/device/hikvision").WithTags("海康ISAPI");
        g.MapGet("/demo-catalog", () => Results.Ok(HikvisionIsapiDemoCatalog.Build())).RequireAuthorization("楼栋管理员");
        g.MapGet("/alert-stream-status", (HikvisionAlertStreamRegistry reg, IOptions<HikvisionIsapiOptions> opt) =>
            Results.Ok(new { code = 0, msg = "成功", data = reg.BuildSnapshot(opt.Value), time = DateTimeOffset.Now }))
            .RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/analyze-response", (HikvisionIsapiAnalyzeReq req) =>
        {
            var summary = HikvisionIsapiResponseStatusHelper.Analyze(req.Raw ?? string.Empty);
            return Results.Ok(new { code = 0, msg = "成功", data = new { summary } });
        }).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");

        g.MapPost("/device-info", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetDeviceInfoAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/connectivity", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.ProbeConnectivityAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/video-inputs/channels", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetVideoInputsChannelsAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/input-proxy/channels", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetInputProxyChannelsAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/input-proxy/channels/status", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetInputProxyChannelsStatusAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/snapshot", async (HikvisionIsapiSnapshotReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.SnapshotAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");

        g.MapPost("/streaming/request-key-frame", async (HikvisionIsapiKeyFrameReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.RequestKeyFrameAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/system/capabilities", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetSystemCapabilitiesJsonAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/event/capabilities", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetEventCapabilitiesAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/content-mgmt/zero-video-channels", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetZeroVideoChannelsAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/traffic/capabilities", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetTrafficCapabilitiesAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/itc/capability", async (HikvisionIsapiDeviceOpReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetItcCapabilityAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");
        g.MapPost("/sdt/picture-upload", async (HikvisionIsapiSdtPictureUploadReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.SdtPictureUploadAsync(req, ct)).RequireAuthorization("楼栋管理员").RequireRateLimiting("HikvisionDeviceApi");

        g.MapPost("/gateway", async (HikvisionIsapiGatewayReq req, HikvisionIsapiGatewayService svc, CancellationToken ct) =>
            await svc.ExecuteAsync(req, ct)).RequireAuthorization("超级管理员").RequireRateLimiting("HikvisionGateway");
    }
}
