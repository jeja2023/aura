/* 文件：生产初始管理员引导 | File: Production admin bootstrapper */
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Internal;

internal static class AdminBootstrapper
{
    public static async Task InitializeAsync(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminBootstrapper));
        var password = (Environment.GetEnvironmentVariable("AURA_ADMIN_PASSWORD") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("生产环境未配置 AURA_ADMIN_PASSWORD，跳过初始管理员创建。");
            return;
        }

        var userName = (Environment.GetEnvironmentVariable("AURA_ADMIN_USER") ?? "admin").Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            userName = "admin";
        }

        try
        {
            var pg = app.Services.GetRequiredService<PgSqlConnectionFactory>();
            await using var conn = pg.CreateConnection();
            await conn.OpenAsync();

            var userCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sys_user");
            if (userCount > 0)
            {
                return;
            }

            await using var tx = await conn.BeginTransactionAsync();
            const string permissions = "[\"all\"]";
            var roleId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO sys_role(role_id, role_name, permission_json, created_at)
                VALUES(1, 'super_admin', CAST(@Permissions AS jsonb), NOW())
                ON CONFLICT (role_id) DO UPDATE
                SET role_name = EXCLUDED.role_name,
                    permission_json = EXCLUDED.permission_json
                RETURNING role_id
                """,
                new { Permissions = permissions },
                tx);

            var hash = BCrypt.Net.BCrypt.HashPassword(password);
            await conn.ExecuteAsync(
                """
                INSERT INTO sys_user(user_name, display_name, password_hash, role_id, status, created_at, last_login_at, must_change_password)
                VALUES(@UserName, @DisplayName, @PasswordHash, @RoleId, 1, NOW(), NULL, FALSE)
                """,
                new
                {
                    UserName = userName,
                    DisplayName = "系统管理员",
                    PasswordHash = hash,
                    RoleId = roleId
                },
                tx);

            await tx.CommitAsync();
            logger.LogInformation("生产环境初始管理员已创建：{UserName}", userName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "生产环境初始管理员创建失败。");
            throw;
        }
    }
}
