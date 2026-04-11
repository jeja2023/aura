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
            var db = app.Services.GetRequiredService<PgSqlStore>();
            var devResetAdminPasswordOnce = app.Configuration.GetValue("Dev:ResetAdminPasswordOnce", false);
            var users = await db.GetUsersAsync();
            if (users.Count == 0 || devResetAdminPasswordOnce)
            {
                var hash = BCrypt.Net.BCrypt.HashPassword("123456");
                bool ok = false;
                if (users.Count == 0)
                {
                    var id = await db.InsertUserAsync("admin", hash, 1);
                    ok = id.HasValue;
                }
                else
                {
                    ok = await db.UpdateUserPasswordByUserNameAsync("admin", hash);
                }
                if (ok)
                {
                    logger.LogInformation("开发环境管理员已配置：用户名=admin, 密码=123456");
                    if (devResetAdminPasswordOnce) TryDisableDevResetFlag(app);
                }
            }
        }
        catch (Exception ex) 
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevInitializer));
            logger.LogError(ex, "开发环境数据初始化失败");
        }
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
        catch { }
    }
}
