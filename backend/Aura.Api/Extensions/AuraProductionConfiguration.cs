using Aura.Api.Ai;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    internal static bool IsPlaceholderValue(string? value, params string[] sentinels)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (var sentinel in sentinels)
        {
            if (!string.IsNullOrWhiteSpace(sentinel) && value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureSafeProductionConfiguration(IConfiguration configuration)
    {
        var jwtKey = configuration["Jwt:Key"];
        var hmacSecret = configuration["Security:HmacSecret"];
        var pgsqlConn = configuration.GetConnectionString("PgSql");
        var redisConn = configuration.GetConnectionString("Redis");
        var allowedHosts = configuration["AllowedHosts"];
        var alertWebhookUrl = configuration["Ops:Alert:WebhookUrl"];

        if (IsPlaceholderValue(jwtKey, "PLEASE_", "REPLACE_", "aura-dev-jwt-key-please-change"))
        {
            throw new InvalidOperationException("生产环境缺少有效的 Jwt:Key 配置。");
        }

        if (IsPlaceholderValue(hmacSecret, "PLEASE_", "REPLACE_", "demo-hmac-secret"))
        {
            throw new InvalidOperationException("生产环境缺少有效的 Security:HmacSecret 配置。");
        }

        if (IsPlaceholderValue(pgsqlConn, "PLEASE_", "REPLACE_", "Password=aura_123456"))
        {
            throw new InvalidOperationException("生产环境缺少有效的 PostgreSQL 连接串配置。");
        }

        if (IsPlaceholderValue(redisConn, "PLEASE_", "REPLACE_"))
        {
            throw new InvalidOperationException("生产环境缺少有效的 Redis 连接串配置。");
        }

        if (IsPlaceholderValue(allowedHosts, "PLEASE_", "REPLACE_", "please-replace", "*"))
        {
            throw new InvalidOperationException("生产环境缺少有效的 AllowedHosts 配置。");
        }

        if (!string.IsNullOrWhiteSpace(alertWebhookUrl))
        {
            if (IsPlaceholderValue(alertWebhookUrl, "PLEASE_", "REPLACE_", "please-replace"))
            {
                throw new InvalidOperationException("生产环境 Ops:Alert:WebhookUrl 仍为占位地址，请替换或留空改用文件告警通道。");
            }

            if (!Uri.TryCreate(alertWebhookUrl, UriKind.Absolute, out var webhookUri) ||
                (webhookUri.Scheme != Uri.UriSchemeHttps && webhookUri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException("生产环境 Ops:Alert:WebhookUrl 必须是有效的 HTTP/HTTPS 绝对地址。");
            }
        }

        var aiBaseUrls = configuration["Ai:BaseUrls"];
        var aiBaseUrl = configuration["Ai:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(aiBaseUrls) || !string.IsNullOrWhiteSpace(aiBaseUrl))
        {
            try
            {
                _ = AiClient.ResolveBaseUrls(aiBaseUrls, aiBaseUrl);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("生产环境 Ai:BaseUrls/Ai:BaseUrl 必须是有效的 HTTP/HTTPS 绝对地址。", ex);
            }
        }

        var hikAlertEnabled = configuration.GetValue<bool>("Hikvision:Isapi:AlertStream:Enabled");
        if (hikAlertEnabled)
        {
            var hikUser = configuration["Hikvision:Isapi:DefaultUserName"];
            var hikPwd = configuration["Hikvision:Isapi:DefaultPassword"];
            var hikUserEnv = configuration["Hikvision:Isapi:DefaultUserNameEnvironmentVariable"];
            var hikPwdEnv = configuration["Hikvision:Isapi:DefaultPasswordEnvironmentVariable"];
            var hasUser = !string.IsNullOrWhiteSpace(hikUser)
                || !string.IsNullOrWhiteSpace(hikUserEnv);
            var hasPwd = !string.IsNullOrWhiteSpace(hikPwd)
                || !string.IsNullOrWhiteSpace(hikPwdEnv);
            if (!hasUser || !hasPwd)
            {
                throw new InvalidOperationException(
                    "生产环境 Hikvision:Isapi:AlertStream:Enabled=true 但未提供 DefaultUserName/DefaultPassword（或对应环境变量入口）。");
            }
        }
    }
}
