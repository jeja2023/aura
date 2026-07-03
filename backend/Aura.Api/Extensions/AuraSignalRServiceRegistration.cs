using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddAuraSignalR(IServiceCollection services, IConfiguration configuration, string redisConn)
    {
        var signalR = services.AddSignalR();
        var signalRBackplaneEnabled = configuration.GetValue<bool>("SignalR:RedisBackplane:Enabled");
        if (!signalRBackplaneEnabled)
        {
            return;
        }

        var signalRRedisConn = configuration["SignalR:RedisBackplane:ConnectionString"];
        if (string.IsNullOrWhiteSpace(signalRRedisConn))
        {
            signalRRedisConn = redisConn;
        }

        if (string.IsNullOrWhiteSpace(signalRRedisConn))
        {
            throw new InvalidOperationException("SignalR Redis Backplane 已启用，但未配置 Redis 连接串。");
        }

        signalR.AddStackExchangeRedis(signalRRedisConn, options =>
        {
            options.Configuration.ChannelPrefix = RedisChannel.Literal(
                configuration["SignalR:RedisBackplane:ChannelPrefix"]?.Trim() ?? "aura:signalr");
        });
    }
}
