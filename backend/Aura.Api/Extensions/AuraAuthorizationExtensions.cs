using Aura.Api.Internal;
using Aura.Api.Data;
using Dapper;
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
                    OnTokenValidated = async context =>
                    {
                        var sid = context.Principal?.FindFirst("sid")?.Value;
                        if (!Guid.TryParse(sid, out var sessionId)) return;
                        var factory = context.HttpContext.RequestServices.GetRequiredService<PgSqlConnectionFactory>();
                        await using var connection = factory.CreateConnection();
                        var active = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                            """
                            SELECT EXISTS(
                              SELECT 1 FROM auth_session s
                              LEFT JOIN sys_user u ON u.user_id=s.user_id
                              WHERE s.session_id=@SessionId AND s.revoked_at IS NULL AND s.expires_at>CURRENT_TIMESTAMP
                                AND (s.user_id IS NULL OR u.status=1))
                            """, new { SessionId = sessionId }, cancellationToken: context.HttpContext.RequestAborted));
                        if (!active)
                        {
                            context.Fail("Session revoked, expired, or disabled");
                            return;
                        }
                        await connection.ExecuteAsync(new CommandDefinition(
                            "UPDATE auth_session SET last_seen_at=CURRENT_TIMESTAMP WHERE session_id=@SessionId AND last_seen_at<CURRENT_TIMESTAMP-INTERVAL '1 minute'",
                            new { SessionId = sessionId }, cancellationToken: context.HttpContext.RequestAborted));
                    },
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
            options.AddPolicy("EventView", policy => RequirePermission(policy, AuraPermissions.EventView));
            options.AddPolicy("EventManage", policy => RequirePermission(policy, AuraPermissions.EventManage));
            options.AddPolicy("CaseView", policy => RequirePermission(policy, AuraPermissions.CaseView));
            options.AddPolicy("CaseManage", policy => RequirePermission(policy, AuraPermissions.CaseManage));
            options.AddPolicy("CaseReview", policy => RequirePermission(policy, AuraPermissions.CaseReview));
            options.AddPolicy("InvestigationView", policy => RequirePermission(policy, AuraPermissions.InvestigationView));
            options.AddPolicy("InvestigationManage", policy => RequirePermission(policy, AuraPermissions.InvestigationManage));
            options.AddPolicy("EvidenceOriginal", policy => RequirePermission(policy, AuraPermissions.EvidenceViewOriginal));
            options.AddPolicy("EvidenceExport", policy => RequirePermission(policy, AuraPermissions.EvidenceExport));
            options.AddPolicy("EvidenceLegalHold", policy => RequirePermission(policy, AuraPermissions.EvidenceLegalHold));
            options.AddPolicy("RuleView", policy => RequirePermission(policy, AuraPermissions.RuleView));
            options.AddPolicy("RuleManage", policy => RequirePermission(policy, AuraPermissions.RuleManage));
            options.AddPolicy("RuleApprove", policy => RequirePermission(policy, AuraPermissions.RuleApprove));
            options.AddPolicy("AiGovernanceView", policy => RequirePermission(policy, AuraPermissions.AiGovernanceView));
            options.AddPolicy("AiGovernanceManage", policy => RequirePermission(policy, AuraPermissions.AiGovernanceManage));
            options.AddPolicy("AiReleaseApprove", policy => RequirePermission(policy, AuraPermissions.AiReleaseApprove));
            options.AddPolicy("IntegrationView", policy => RequirePermission(policy, AuraPermissions.IntegrationView));
            options.AddPolicy("IntegrationManage", policy => RequirePermission(policy, AuraPermissions.IntegrationManage));
            options.AddPolicy("IntegrationTest", policy => RequirePermission(policy, AuraPermissions.IntegrationTest));
            options.AddPolicy("OpsView", policy => RequirePermission(policy, AuraPermissions.OpsView));
            options.AddPolicy("OpsExecute", policy => RequirePermission(policy, AuraPermissions.OpsExecute));
            options.AddPolicy("OpsHighImpact", policy => RequirePermission(policy, AuraPermissions.OpsHighImpact));
            options.AddPolicy("UsageView", policy => RequirePermission(policy, AuraPermissions.UsageView));
            options.AddPolicy("UsageManage", policy => RequirePermission(policy, AuraPermissions.UsageManage));
            options.AddPolicy("DataGovernanceView", policy => RequirePermission(policy, AuraPermissions.DataGovernanceView));
            options.AddPolicy("DataGovernanceManage", policy => RequirePermission(policy, AuraPermissions.DataGovernanceManage));
        });
    }

    private static void RequirePermission(AuthorizationPolicyBuilder policy, string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => AuraPermissions.HasPermission(context.User, permission));
    }
}
