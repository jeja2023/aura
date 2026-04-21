/* 文件：用户与认证仓储 | File: User and auth repository */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class UserAuthRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<UserAuthRepository>? _logger;

    public UserAuthRepository(PgSqlConnectionFactory connectionFactory, ILogger<UserAuthRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

    public async Task<DbUser?> FindUserAsync(string userName)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.QueryFirstOrDefaultAsync<DbUser>(
                """
                SELECT u.user_name AS UserName, u.password_hash AS PasswordHash, r.role_name AS RoleName,
                       u.must_change_password AS MustChangePassword
                FROM sys_user u
                LEFT JOIN sys_role r ON u.role_id = r.role_id
                WHERE u.user_name = @userName AND u.status = 1
                LIMIT 1
                """,
                new { userName });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询用户失败。userName={UserName}", userName);
            return null;
        }
    }

    public async Task<List<DbRole>> GetRolesAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbRole>(
                """
                SELECT role_id AS RoleId, role_name AS RoleName, COALESCE(CAST(permission_json AS TEXT), '[]') AS PermissionJson
                FROM sys_role
                ORDER BY role_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询角色列表失败。");
            return [];
        }
    }

    public async Task<long?> InsertRoleAsync(string roleName, string permissionJson)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO sys_role(role_name, permission_json, created_at)
                VALUES(@RoleName, CAST(@PermissionJson AS jsonb), NOW())
                RETURNING role_id
                """,
                new { RoleName = roleName, PermissionJson = permissionJson });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入角色失败。roleName={RoleName}", roleName);
            return null;
        }
    }

    public async Task<List<DbUserListItem>> GetUsersAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync<DbUserListItem>(
                """
                SELECT u.user_id AS UserId, u.user_name AS UserName, CAST(u.status AS BIGINT) AS Status,
                       COALESCE(NULLIF(u.display_name, ''), u.user_name) AS DisplayName,
                       r.role_name AS RoleName, u.role_id AS RoleId, u.created_at AS CreatedAt,
                       u.last_login_at AS LastLoginAt, u.must_change_password AS MustChangePassword
                FROM sys_user u
                LEFT JOIN sys_role r ON u.role_id = r.role_id
                ORDER BY u.user_id DESC
                """);
            return rows.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询用户列表失败。");
            return [];
        }
    }

    public async Task<(List<DbUserListItem> Rows, int Total)> GetUsersAsync(string? keyword, int page, int pageSize)
    {
        try
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 20;

            await using var conn = CreateConnection();
            var trimmedKeyword = keyword?.Trim();
            var filter = string.IsNullOrWhiteSpace(trimmedKeyword)
                ? ""
                : " WHERE u.user_name LIKE @kw OR COALESCE(NULLIF(u.display_name, ''), u.user_name) LIKE @kw ";
            var total = await conn.ExecuteScalarAsync<int>(
                $"""
                SELECT COUNT(1)
                FROM sys_user u
                {filter}
                """,
                new { kw = $"%{trimmedKeyword}%" });

            if (total <= 0)
            {
                return ([], 0);
            }

            var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            var actualPage = Math.Min(Math.Max(1, page), maxPage);
            var offset = (actualPage - 1) * pageSize;
            var rows = await conn.QueryAsync<DbUserListItem>(
                $"""
                SELECT u.user_id AS UserId, u.user_name AS UserName, CAST(u.status AS BIGINT) AS Status,
                       COALESCE(NULLIF(u.display_name, ''), u.user_name) AS DisplayName,
                       r.role_name AS RoleName, u.role_id AS RoleId, u.created_at AS CreatedAt,
                       u.last_login_at AS LastLoginAt, u.must_change_password AS MustChangePassword
                FROM sys_user u
                LEFT JOIN sys_role r ON u.role_id = r.role_id
                {filter}
                ORDER BY u.user_id DESC
                LIMIT @pageSize OFFSET @offset
                """,
                new
                {
                    kw = $"%{trimmedKeyword}%",
                    pageSize,
                    offset
                });
            return (rows.ToList(), total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库分页查询用户列表失败。keyword={Keyword}, page={Page}, pageSize={PageSize}", keyword, page, pageSize);
            return ([], 0);
        }
    }

    public async Task<long?> InsertUserAsync(string userName, string displayName, string passwordHash, long roleId)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO sys_user(user_name, display_name, password_hash, role_id, status, created_at, last_login_at, must_change_password)
                VALUES(@UserName, @DisplayName, @PasswordHash, @RoleId, 1, NOW(), NULL, FALSE)
                RETURNING user_id
                """,
                new { UserName = userName, DisplayName = displayName, PasswordHash = passwordHash, RoleId = roleId });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入用户失败。userName={UserName}, roleId={RoleId}", userName, roleId);
            return null;
        }
    }

    public async Task<bool> UpdateUserPasswordByUserNameAsync(string userName, string passwordHash, bool mustChangePassword)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE sys_user
                SET password_hash=@PasswordHash, must_change_password=@MustChangePassword
                WHERE user_name=@UserName
                """,
                new { UserName = userName, PasswordHash = passwordHash, MustChangePassword = mustChangePassword });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库重置用户密码失败。userName={UserName}", userName);
            return false;
        }
    }

    public async Task<bool> UpdateUserStatusAsync(long userId, int status)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                "UPDATE sys_user SET status=@Status WHERE user_id=@UserId",
                new { Status = status, UserId = userId });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库更新用户状态失败。userId={UserId}, status={Status}", userId, status);
            return false;
        }
    }

    public async Task<int> UpdateUserProfileAsync(long userId, string userName, string displayName, long roleId, int status)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteAsync(
                """
                UPDATE sys_user
                SET user_name=@UserName, display_name=@DisplayName, role_id=@RoleId, status=@Status
                WHERE user_id=@UserId
                """,
                new { UserName = userName, DisplayName = displayName, RoleId = roleId, Status = status, UserId = userId });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库更新用户资料失败。userId={UserId}", userId);
            throw;
        }
    }

    public async Task<bool> UpdateUserPasswordByUserIdAsync(long userId, string passwordHash, bool mustChangePassword)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE sys_user
                SET password_hash=@PasswordHash, must_change_password=@MustChangePassword
                WHERE user_id=@UserId
                """,
                new { PasswordHash = passwordHash, UserId = userId, MustChangePassword = mustChangePassword });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库按用户ID重置密码失败。userId={UserId}", userId);
            return false;
        }
    }

    public async Task<bool> DeleteUserAsync(long userId)
    {
        try
        {
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                "DELETE FROM sys_user WHERE user_id=@UserId",
                new { UserId = userId });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库删除用户失败。userId={UserId}", userId);
            return false;
        }
    }

    public async Task<bool> UpdateUserLastLoginByUserNameAsync(string userName, DateTimeOffset loginAt)
    {
        try
        {
            var loginAtUtc = loginAt.ToUniversalTime();
            await using var conn = CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE sys_user
                SET last_login_at=@LastLoginAt
                WHERE user_name=@UserName
                """,
                new { UserName = userName, LastLoginAt = loginAtUtc });
            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "数据库更新最后登录时间失败。userName={UserName}", userName);
            return false;
        }
    }
}
