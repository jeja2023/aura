/* 文件：开发环境初始化（DevInitializer.cs） | File: Development initializer */
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Api.Data;

namespace Aura.Api.Internal;

internal static class DevInitializer
{
    public static async Task InitializeDevDataAsync(WebApplication app)
    {
        try
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevInitializer));
            var usersRepository = app.Services.GetRequiredService<UserAuthRepository>();
            var resetAdminPasswordOnce = app.Configuration.GetValue("Dev:ResetAdminPasswordOnce", false);
            var users = await usersRepository.GetUsersAsync();
            if (users.Count == 0 || resetAdminPasswordOnce)
            {
                var nextPassword = ResolveDevAdminPassword();
                var hash = BCrypt.Net.BCrypt.HashPassword(nextPassword);
                var ok = false;
                if (users.Count == 0)
                {
                    var id = await usersRepository.InsertUserAsync("admin", "系统管理员", hash, 1);
                    ok = id.HasValue;
                }
                else
                {
                    ok = await usersRepository.UpdateUserPasswordByUserNameAsync("admin", hash, mustChangePassword: false);
                }

                if (ok)
                {
                    if (Environment.GetEnvironmentVariable("AURA_ADMIN_PASSWORD") is { Length: > 0 })
                    {
                        logger.LogInformation("开发环境管理员已配置：用户名 admin，密码已使用环境变量 AURA_ADMIN_PASSWORD。");
                    }
                    else
                    {
                        logger.LogInformation("开发环境管理员已配置：用户名 admin，临时密码 {Password}", nextPassword);
                    }

                    if (resetAdminPasswordOnce)
                    {
                        TryDisableDevResetFlag(app);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevInitializer));
            logger.LogError(ex, "开发环境数据初始化失败");
        }
    }

    private static string ResolveDevAdminPassword()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("AURA_ADMIN_PASSWORD") ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_+=";
        var all = upper + lower + digits + symbols;
        var chars = new[]
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        }.ToList();

        while (chars.Count < 16)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());
    }

    private static void TryDisableDevResetFlag(WebApplication app)
    {
        try
        {
            var path = Path.Combine(app.Environment.ContentRootPath, "appsettings.Development.json");
            if (!File.Exists(path)) return;
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node?["Dev"] is JsonObject dev)
            {
                dev["ResetAdminPasswordOnce"] = false;
                File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // 开发环境下忽略回写失败，避免影响主流程
        }
    }
}
