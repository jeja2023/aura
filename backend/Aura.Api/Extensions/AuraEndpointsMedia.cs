/* 文件：媒体取流规划端点（AuraEndpointsMedia.cs） | File: Media streaming planning endpoints */
using Aura.Api.Models;
using Aura.Api.Services.Hikvision;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsMedia
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var g = app.MapGroup("/api/media").WithTags("媒体与取流");

        g.MapGet("/capabilities", () => Results.Ok(new
        {
            code = 0,
            msg = "媒体子系统能力说明（规划项）",
            data = new
            {
                live = "实况：RTSP 拉流 → 转封装（HLS/WebRTC）由独立媒体服务承担，不在 Aura.Api 进程内转发。",
                playback = "回放：依赖 NVR ISAPI/私有 SDK 或录像文件索引，与 ISAPI 单次 HTTP 封装分层。",
                hints = "POST /api/media/hikvision/stream-hint 仅返回路径模板与通道号，不返回凭据、不建立媒体会话。"
            }
        })).RequireAuthorization("楼栋管理员");

        g.MapPost("/hikvision/stream-hint", async (MediaStreamHintReq req, HikvisionNvrIntegrationService svc, CancellationToken ct) =>
            await svc.GetMediaStreamHintAsync(req, ct)).RequireAuthorization("楼栋管理员");
    }
}
