/* 文件：认证与账号端点 | File: Auth and account endpoints */
using System.Security.Claims;
using Aura.Api.Internal;
using Aura.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsAuth
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var cache = ctx.Cache;

        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", async (HttpContext http, LoginReq req, IdentityAdminService svc) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var dim = AuraHelpers.Sanitize($"{ip}|{req.UserName ?? ""}");
            var rl = await AuraHelpers.CheckRateLimitAsync(http.Request, cache, "auth.login", 20, TimeSpan.FromMinutes(1), dim);
            if (rl is not null) return rl;
            return await svc.LoginAsync(http, req);
        });
        auth.MapPost("/logout", (HttpContext http, IdentityAdminService svc) => svc.Logout(http));
        auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            code = 0,
            msg = "查询成功",
            data = new
            {
                userName = user.Identity?.Name,
                role = user.FindFirst(ClaimTypes.Role)?.Value,
                mustChangePassword = string.Equals(user.FindFirst(AuraHelpers.MustChangePasswordClaimType)?.Value, "true", StringComparison.OrdinalIgnoreCase)
            }
        })).RequireAuthorization();
        auth.MapPost("/change-password", async (HttpContext http, ChangePasswordReq req, IdentityAdminService svc) =>
            await svc.ChangePasswordAsync(http, req)).RequireAuthorization();

        var roleGroup = app.MapGroup("/api/role");
        roleGroup.MapGet("/list", async (IdentityAdminService svc) => await svc.GetRolesAsync()).RequireAuthorization("超级管理员");
        roleGroup.MapPost("/create", async (RoleCreateReq req, IdentityAdminService svc) => await svc.CreateRoleAsync(req)).RequireAuthorization("超级管理员");

        var userGroup = app.MapGroup("/api/user");
        userGroup.MapGet("/list", async (UserQueryService svc, string? keyword, int page = 1, int pageSize = 20) =>
        {
            var result = await svc.GetUsersAsync(keyword, page, pageSize);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Data, pager = result.Pager });
        }).RequireAuthorization("超级管理员");
        userGroup.MapPost("/create", async (UserCreateReq req, IdentityAdminService svc) => await svc.CreateUserAsync(req)).RequireAuthorization("超级管理员");
        userGroup.MapPut("/{userId:long}", async (long userId, UserUpdateReq req, IdentityAdminService svc) => await svc.UpdateUserAsync(userId, req)).RequireAuthorization("超级管理员");
        userGroup.MapPost("/{userId:long}/password", async (long userId, UserPasswordResetReq req, IdentityAdminService svc) => await svc.ResetUserPasswordAsync(userId, req)).RequireAuthorization("超级管理员");
        userGroup.MapPost("/status/{userId:long}", async (long userId, UserStatusReq req, IdentityAdminService svc) => await svc.UpdateUserStatusAsync(userId, req)).RequireAuthorization("超级管理员");
        userGroup.MapDelete("/{userId:long}", async (long userId, IdentityAdminService svc) => await svc.DeleteUserAsync(userId)).RequireAuthorization("超级管理员");
    }
}
