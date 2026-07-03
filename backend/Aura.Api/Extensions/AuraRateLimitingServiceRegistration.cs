using Aura.Api.Internal;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddAuraRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                await AuraApiResults.WriteErrorAsync(
                    context.HttpContext.Response,
                    StatusCodes.Status429TooManyRequests,
                    "请求过于频繁，请稍后再试",
                    42901,
                    cancellationToken: token);
            };
            options.AddPolicy("HikvisionGateway", context =>
            {
                var config = context.RequestServices.GetRequiredService<IConfiguration>();
                var rpm = config.GetValue("Hikvision:Isapi:GatewayMaxRequestsPerMinute", 0);
                if (rpm <= 0)
                {
                    return RateLimitPartition.GetNoLimiter("hikvision_gateway_unlimited");
                }

                var key = context.User?.Identity?.Name
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = rpm,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    });
            });
            options.AddPolicy("HikvisionDeviceApi", context =>
            {
                var config = context.RequestServices.GetRequiredService<IConfiguration>();
                var rpm = config.GetValue("Hikvision:Isapi:DeviceApiMaxRequestsPerMinute", 0);
                if (rpm <= 0)
                {
                    return RateLimitPartition.GetNoLimiter("hikvision_device_api_unlimited");
                }

                var key = context.User?.Identity?.Name
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = rpm,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    });
            });
        });
    }
}
