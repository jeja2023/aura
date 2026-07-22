using Aura.Api.Internal;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddAuraAuthenticationAndAuthorization(IServiceCollection services, string jwtKey, string jwtIssuer, string jwtAudience)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Token))
                        {
                            var cookieToken = context.Request.Cookies["aura_token"];
                            if (!string.IsNullOrWhiteSpace(cookieToken))
                            {
                                context.Token = cookieToken;
                            }
                        }

                        var path = context.HttpContext.Request.Path;
                        if (string.IsNullOrWhiteSpace(context.Token)
                            && path.StartsWithSegments("/hubs/events", StringComparison.OrdinalIgnoreCase))
                        {
                            var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(accessToken))
                            {
                                context.Token = accessToken;
                            }
                        }

                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        if (context.Response.HasStarted)
                        {
                            return;
                        }

                        context.HandleResponse();
                        await AuraApiResults.WriteErrorAsync(
                            context.Response,
                            StatusCodes.Status401Unauthorized,
                            "未登录或登录已过期，请重新登录",
                            40100);
                    },
                    OnForbidden = async context =>
                    {
                        if (context.Response.HasStarted)
                        {
                            return;
                        }

                        await AuraApiResults.WriteErrorAsync(
                            context.Response,
                            StatusCodes.Status403Forbidden,
                            "无权限访问该资源",
                            40300);
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("超级管理员", policy => policy.RequireRole("super_admin"));
            options.AddPolicy("楼栋管理员", policy => policy.RequireRole("building_admin", "super_admin"));
            options.AddPolicy("告警操作", policy => RequirePermission(policy, AuraPermissions.AlertManage));
            options.AddPolicy("AI配置", policy => RequirePermission(policy, AuraPermissions.AiSettings));
            options.AddPolicy("设备诊断", policy => RequirePermission(policy, AuraPermissions.DeviceDiagnostics));
            options.AddPolicy("数据导出", policy => RequirePermission(policy, AuraPermissions.Export));
            options.AddPolicy("ReportManage", policy => RequirePermission(policy, AuraPermissions.ReportManage));
            options.AddPolicy("SpaceManage", policy => RequirePermission(policy, AuraPermissions.SpaceManage));
            options.AddPolicy("TenantManage", policy => RequirePermission(policy, AuraPermissions.TenantManage));
            options.AddPolicy("AiPlatform", policy => RequirePermission(policy, AuraPermissions.AiPlatform));
            options.AddPolicy("MediaAnalysisView", policy => RequirePermission(policy, AuraPermissions.MediaAnalysisView));
            options.AddPolicy("MediaAnalysisManage", policy => RequirePermission(policy, AuraPermissions.MediaAnalysisManage));
            options.AddPolicy("MediaAnalysisOperate", policy => RequirePermission(policy, AuraPermissions.MediaAnalysisOperate));
            options.AddPolicy("MediaAnalysisReplay", policy => RequirePermission(policy, AuraPermissions.MediaAnalysisReplay));
            options.AddPolicy("VectorIndexManage", policy => RequirePermission(policy, AuraPermissions.VectorIndexManage));
            options.AddPolicy("GraphView", policy => RequirePermission(policy, AuraPermissions.GraphView));
            options.AddPolicy("GraphAdmin", policy => RequirePermission(policy, AuraPermissions.GraphAdmin));
        });
    }

    private static void RequirePermission(AuthorizationPolicyBuilder policy, string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => AuraPermissions.HasPermission(context.User, permission));
    }
}
