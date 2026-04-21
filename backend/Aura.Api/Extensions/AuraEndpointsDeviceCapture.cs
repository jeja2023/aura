using System.Text.Json;
using Aura.Api.Capture;
using Aura.Api.Capture.Adapters;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsDeviceCapture
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var devices = ctx.Devices;
        var campusResources = ctx.CampusResources;
        var audit = ctx.Audit;
        var cache = ctx.Cache;
        var store = ctx.Store;
        var isDev = ctx.IsDev;
        var globalHmacSecret = ctx.GlobalHmacSecret;
        var captureIpWhitelist = ctx.CaptureIpWhitelist;

        var cameraGroup = app.MapGroup("/api/camera");
        cameraGroup.MapGet("/list", async () =>
        {
            var rows = await campusResources.GetCamerasAsync();
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!ctx.AllowInMemoryFallback) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbCamera>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Cameras.OrderByDescending(x => x.CameraId) });
        }).RequireAuthorization("楼栋管理员");

        cameraGroup.MapPost("/create", async (CameraCreateReq req) =>
        {
            var dbId = await campusResources.InsertCameraAsync(req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
            if (dbId.HasValue)
            {
                await audit.InsertOperationAsync("楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
                return Results.Ok(new { code = 0, msg = "创建成功", data = new { cameraId = dbId.Value, req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY } });
            }

            if (!ctx.AllowInMemoryFallback) return AuraApiResults.ServiceUnavailable("数据库写入失败，无法创建摄像头布点", 50301);
            var entity = new CameraEntity(Interlocked.Increment(ref store.CameraSeed), req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
            store.Cameras.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        var deviceGroup = app.MapGroup("/api/device");
        deviceGroup.MapGet("/list", async (DeviceManagementService svc) => await svc.GetDevicesAsync()).RequireAuthorization("楼栋管理员");
        deviceGroup.MapPost("/register", async (DeviceRegisterReq req, DeviceManagementService svc) => await svc.RegisterDeviceAsync(req)).RequireAuthorization("超级管理员");
        deviceGroup.MapPost("/ping/{deviceId:long}", (long deviceId, DeviceManagementService svc) => svc.PingDevice(deviceId)).RequireAuthorization("楼栋管理员");

        var captureGroup = app.MapGroup("/api/capture");
        captureGroup.MapPost("/push", async (HttpContext http, CaptureProcessingService svc) =>
        {
            var reqJson = await http.Request.ReadFromJsonAsync<JsonElement>();
            var normalized = new HikvisionIsapiAdapter().Normalize(reqJson);
            var validation = await AuraHelpers.ValidateCaptureRequest(
                http.Request,
                normalized,
                devices,
                isDev,
                globalHmacSecret,
                captureIpWhitelist,
                AuraEndpointContext.MaxCaptureRequestBytes,
                AuraEndpointContext.MaxImageBase64Chars,
                AuraEndpointContext.MaxMetadataJsonChars,
                cache);
            return validation ?? await svc.ProcessAsync(normalized, "海康 ISAPI 抓拍");
        });

        captureGroup.MapPost("/sdk", async (HttpContext http, CaptureProcessingService svc) =>
        {
            var reqJson = await http.Request.ReadFromJsonAsync<JsonElement>();
            var normalized = new CppSdkAdapter().Normalize(reqJson);
            var validation = await AuraHelpers.ValidateCaptureRequest(
                http.Request,
                normalized,
                devices,
                isDev,
                globalHmacSecret,
                captureIpWhitelist,
                AuraEndpointContext.MaxCaptureRequestBytes,
                AuraEndpointContext.MaxImageBase64Chars,
                AuraEndpointContext.MaxMetadataJsonChars,
                cache);
            return validation ?? await svc.ProcessAsync(normalized, "C++SDK 抓拍");
        });

        captureGroup.MapPost("/onvif", async (HttpContext http, CaptureProcessingService svc) =>
        {
            var reqJson = await http.Request.ReadFromJsonAsync<JsonElement>();
            var normalized = new OnvifAdapter().Normalize(reqJson);
            var validation = await AuraHelpers.ValidateCaptureRequest(
                http.Request,
                normalized,
                devices,
                isDev,
                globalHmacSecret,
                captureIpWhitelist,
                AuraEndpointContext.MaxCaptureRequestBytes,
                AuraEndpointContext.MaxImageBase64Chars,
                AuraEndpointContext.MaxMetadataJsonChars,
                cache);
            return validation ?? await svc.ProcessAsync(normalized, "ONVIF 抓拍");
        });

        captureGroup.MapPost("/mock", async (CaptureMockReq req, CaptureOpsService svc) => await svc.CreateMockAsync(req)).RequireAuthorization("楼栋管理员");
        captureGroup.MapGet("/list", async (HttpRequest httpReq, CaptureOpsService svc) => await svc.GetCapturesAsync(httpReq)).RequireAuthorization("楼栋管理员");

        var retryGroup = app.MapGroup("/api/retry");
        retryGroup.MapGet("/status", async (RetryProcessingService svc) => await svc.GetStatusAsync()).RequireAuthorization("超级管理员");
        retryGroup.MapPost("/process", async (HttpRequest request, RetryProcessReq req, RetryProcessingService svc) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "retry.process", 30, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.ProcessAsync(req);
        }).RequireAuthorization("超级管理员");
    }
}
