using System.IdentityModel.Tokens.Jwt;
using Aura.Api.Models;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Serialization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Aura.Api;

internal sealed class IdentityAdminService
{
    private const string DefaultResetPassword = "Aura@123456";
    private readonly AppStore _store;
    private readonly PgSqlStore _db;
    private readonly RedisCacheService _cache;
    private readonly ILogger<IdentityAdminService> _logger;
    private readonly string _jwtKey;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly int _jwtExpireMinutes;

    public IdentityAdminService(
        AppStore store,
        PgSqlStore db,
        RedisCacheService cache,
        ILogger<IdentityAdminService> logger,
        string jwtKey,
        string jwtIssuer,
        string jwtAudience,
        int jwtExpireMinutes)
    {
        _store = store;
        _db = db;
        _cache = cache;
        _logger = logger;
        _jwtKey = jwtKey;
        _jwtIssuer = jwtIssuer;
        _jwtAudience = jwtAudience;
        _jwtExpireMinutes = jwtExpireMinutes;
    }

    public async Task<IResult> LoginAsync(HttpContext http, LoginReq req)
    {
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
        {
            return Results.BadRequest(new { code = 40001, msg = "用户名或密码不能为空" });
        }

        var dbUser = await _db.FindUserAsync(req.UserName);
        if (dbUser is null || !BCrypt.Net.BCrypt.Verify(req.Password, dbUser.PasswordHash))
        {
            var failIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await _db.InsertSystemLogAsync("警告", "认证服务", $"登录失败，用户名={req.UserName}, IP={failIp}");
            _logger.LogWarning("登录失败：用户名或密码错误。用户：{UserName}", req.UserName);
            return Results.BadRequest(new { code = 40003, msg = "用户名或密码错误" });
        }

        var role = AuraHelpers.ConvertRole(dbUser.RoleName);
        var loginAt = DateTimeOffset.Now;
        var lastLoginUpdated = await _db.UpdateUserLastLoginByUserNameAsync(req.UserName, loginAt);
        if (lastLoginUpdated)
        {
            await _cache.DeleteAsync("user:list:v2");
        }
        var userIdx = _store.Users.FindIndex(u => string.Equals(u.UserName, req.UserName, StringComparison.OrdinalIgnoreCase));
        if (userIdx >= 0)
        {
            _store.Users[userIdx] = _store.Users[userIdx] with { LastLoginAt = loginAt };
        }
        var token = BuildJwtToken(req.UserName, role);
        http.Response.Cookies.Append("aura_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(_jwtExpireMinutes)
        });
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await _db.InsertOperationAsync(req.UserName, "用户登录", $"角色={role}, IP={ip}");
        await _db.InsertSystemLogAsync("信息", "认证服务", $"用户登录成功，用户名={req.UserName}, 角色={role}, IP={ip}");
        _logger.LogInformation("用户登录成功：{UserName}, 角色：{Role}", req.UserName, role);
        return Results.Ok(new
        {
            code = 0,
            msg = "登录成功",
            data = new
            {
                token,
                expireAt = DateTimeOffset.Now.AddMinutes(_jwtExpireMinutes),
                userName = req.UserName,
                role
            }
        });
    }

    public IResult Logout(HttpContext http)
    {
        var userName = http.User?.Identity?.Name;
        var operatorName = string.IsNullOrWhiteSpace(userName) ? "匿名用户" : userName;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _ = _db.InsertOperationAsync(operatorName, "用户退出", $"IP={ip}");
        _ = _db.InsertSystemLogAsync("信息", "认证服务", $"用户退出登录，用户名={operatorName}, IP={ip}");
        http.Response.Cookies.Append("aura_token", string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UnixEpoch
        });
        _logger.LogInformation("用户已退出登录");
        return Results.Ok(new { code = 0, msg = "已退出登录" });
    }

    public async Task<IResult> GetRolesAsync()
    {
        var cached = await _cache.GetAsync("role:list");
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var cacheRows = JsonSerializer.Deserialize<List<DbRole>>(cached, AuraJsonSerializerOptions.Default);
            if (cacheRows is { Count: > 0 })
            {
                return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
            }
        }

        var rows = await _db.GetRolesAsync();
        if (rows.Count > 0)
        {
            await _cache.SetAsync("role:list", JsonSerializer.Serialize(rows, AuraJsonSerializerOptions.Default), TimeSpan.FromMinutes(5));
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }

        var mockRows = _store.Roles.OrderByDescending(x => x.RoleId).ToList();
        return Results.Ok(new { code = 0, msg = "查询成功", data = mockRows });
    }

    public async Task<IResult> CreateRoleAsync(RoleCreateReq req)
    {
        if (string.IsNullOrWhiteSpace(req.RoleName))
        {
            return Results.BadRequest(new { code = 40011, msg = "角色名不能为空" });
        }

        var permissionJson = req.PermissionJson ?? "[]";
        var dbId = await _db.InsertRoleAsync(req.RoleName, permissionJson);
        if (dbId.HasValue)
        {
            await _db.InsertOperationAsync("系统管理员", "角色创建", $"角色={req.RoleName}");
            _logger.LogInformation("角色创建成功：{RoleName}", req.RoleName);
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { roleId = dbId.Value, roleName = req.RoleName, permissionJson } });
        }

        var entity = new RoleEntity(Interlocked.Increment(ref _store.RoleSeed), req.RoleName, permissionJson);
        _store.Roles.Add(entity);
        AddOperationLog("系统管理员", "角色创建", $"角色={req.RoleName}");
        _logger.LogWarning("数据库创建角色失败，已存入内存库：{RoleName}", req.RoleName);
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> GetUsersAsync()
    {
        var cached = await _cache.GetAsync("user:list:v2");
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var cacheRows = JsonSerializer.Deserialize<List<DbUserListItem>>(cached, AuraJsonSerializerOptions.Default);
            if (cacheRows is { Count: > 0 })
            {
                return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
            }
        }

        var rows = await _db.GetUsersAsync();
        if (rows.Count > 0)
        {
            await _cache.SetAsync("user:list:v2", JsonSerializer.Serialize(rows, AuraJsonSerializerOptions.Default), TimeSpan.FromMinutes(5));
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }

        var mockRows = _store.Users
            .OrderByDescending(x => x.UserId)
            .Select(u => new DbUserListItem(u.UserId, u.UserName, u.Status, u.DisplayName, u.RoleName, u.RoleId, u.CreatedAt.DateTime, u.LastLoginAt?.DateTime))
            .ToList();
        return Results.Ok(new { code = 0, msg = "查询成功", data = mockRows });
    }

    public async Task<IResult> CreateUserAsync(UserCreateReq req)
    {
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
        {
            return Results.BadRequest(new { code = 40012, msg = "用户名或密码不能为空" });
        }
        var displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.UserName.Trim() : req.DisplayName.Trim();
        if (displayName.Length > 64)
            return Results.BadRequest(new { code = 40018, msg = "显示名称过长" });

        var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);
        var dbId = await _db.InsertUserAsync(req.UserName, displayName, hash, req.RoleId);
        if (dbId.HasValue)
        {
            await _cache.DeleteAsync("user:list:v2");
            await _db.InsertOperationAsync("系统管理员", "用户创建", $"用户={req.UserName}, 角色ID={req.RoleId}");
            _logger.LogInformation("用户创建成功：{UserName}", req.UserName);
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { userId = dbId.Value, userName = req.UserName, displayName, roleId = req.RoleId, status = 1 } });
        }

        var entity = new UserEntity(
            UserId: Interlocked.Increment(ref _store.UserSeed),
            UserName: req.UserName,
            DisplayName: displayName,
            RoleName: req.RoleId == 1 ? "super_admin" : "building_admin",
            RoleId: req.RoleId,
            Status: 1,
            CreatedAt: DateTimeOffset.Now,
            LastLoginAt: null);
        _store.Users.Add(entity);
        AddOperationLog("系统管理员", "用户创建", $"用户={req.UserName}, 角色ID={req.RoleId}");
        _logger.LogWarning("数据库创建用户失败，已存入内存库：{UserName}", req.UserName);
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> UpdateUserStatusAsync(long userId, UserStatusReq req)
    {
        var ok = await _db.UpdateUserStatusAsync(userId, req.Status);
        if (ok)
        {
            await _cache.DeleteAsync("user:list:v2");
            await _db.InsertOperationAsync("系统管理员", "用户状态更新", $"用户ID={userId}, 状态={req.Status}");
            _logger.LogInformation("用户状态更新成功：ID={UserId}, 状态={Status}", userId, req.Status);
            return Results.Ok(new { code = 0, msg = "状态更新成功" });
        }

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
        {
            _logger.LogWarning("用户状态更新失败：用户ID {UserId} 不存在", userId);
            return Results.NotFound(new { code = 40402, msg = "用户不存在" });
        }

        var entity = _store.Users[uidx];
        var updated = entity with { Status = req.Status };
        _store.Users[uidx] = updated;
        await _cache.DeleteAsync("user:list:v2");
        AddOperationLog("系统管理员", "用户状态更新", $"用户ID={userId}, 状态={req.Status}");
        _logger.LogInformation("内存库用户状态已更新：ID={UserId}, 状态={Status}", userId, req.Status);
        return Results.Ok(new { code = 0, msg = "状态更新成功", data = updated });
    }

    public async Task<IResult> UpdateUserAsync(long userId, UserUpdateReq req)
    {
        if (string.IsNullOrWhiteSpace(req.UserName))
            return Results.BadRequest(new { code = 40013, msg = "用户名不能为空" });
        var name = req.UserName.Trim();
        var displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? name : req.DisplayName.Trim();
        if (name.Length > 64)
            return Results.BadRequest(new { code = 40016, msg = "用户名过长" });
        if (displayName.Length > 64)
            return Results.BadRequest(new { code = 40018, msg = "显示名称过长" });
        if (req.RoleId != 1 && req.RoleId != 2)
            return Results.BadRequest(new { code = 40014, msg = "角色无效" });
        if (req.Status != 0 && req.Status != 1)
            return Results.BadRequest(new { code = 40015, msg = "状态无效" });

        var roleName = req.RoleId == 1 ? "super_admin" : "building_admin";

        try
        {
            var n = await _db.UpdateUserProfileAsync(userId, name, displayName, req.RoleId, req.Status);
            if (n > 0)
            {
                await _cache.DeleteAsync("user:list:v2");
                await _db.InsertOperationAsync(
                    "系统管理员",
                    "用户资料更新",
                    $"用户ID={userId}, 用户名={name}, 显示名称={displayName}, 角色ID={req.RoleId}, 状态={req.Status}");
                _logger.LogInformation("用户资料已更新：UserId={UserId}", userId);
                return Results.Ok(new { code = 0, msg = "保存成功" });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Json(new { code = 40901, msg = "用户名已被占用" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库更新用户资料异常。userId={UserId}", userId);
            return Results.Problem("保存失败");
        }

        if (_store.Users.Any(u => u.UserId != userId && string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)))
            return Results.Json(new { code = 40901, msg = "用户名已被占用" }, statusCode: StatusCodes.Status409Conflict);

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
            return Results.NotFound(new { code = 40402, msg = "用户不存在" });

        var prev = _store.Users[uidx];
        _store.Users[uidx] = prev with
        {
            UserName = name,
            DisplayName = displayName,
            RoleName = roleName,
            RoleId = req.RoleId,
            Status = req.Status
        };
        await _cache.DeleteAsync("user:list:v2");
        AddOperationLog("系统管理员", "用户资料更新", $"用户ID={userId}, 用户名={name}, 显示名称={displayName}, 角色ID={req.RoleId}, 状态={req.Status}");
        return Results.Ok(new { code = 0, msg = "保存成功", data = _store.Users[uidx] });
    }

    public async Task<IResult> ResetUserPasswordAsync(long userId, UserPasswordResetReq _req)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(DefaultResetPassword);
        if (await _db.UpdateUserPasswordByUserIdAsync(userId, hash))
        {
            await _cache.DeleteAsync("user:list:v2");
            await _db.InsertOperationAsync("系统管理员", "用户密码重置", $"用户ID={userId}");
            _logger.LogInformation("用户密码已重置：UserId={UserId}", userId);
            return Results.Ok(new { code = 0, msg = "密码已重置为默认密钥", data = new { defaultPassword = DefaultResetPassword } });
        }

        if (_store.Users.All(x => x.UserId != userId))
            return Results.NotFound(new { code = 40402, msg = "用户不存在" });

        _logger.LogWarning("内存库用户未存储密码哈希，跳过持久化重置：UserId={UserId}", userId);
        return Results.Ok(new { code = 0, msg = "密码已重置为默认密钥（内存模式未写入哈希）", data = new { defaultPassword = DefaultResetPassword } });
    }

    public async Task<IResult> DeleteUserAsync(long userId)
    {
        var ok = await _db.DeleteUserAsync(userId);
        if (ok)
        {
            await _cache.DeleteAsync("user:list:v2");
            await _db.InsertOperationAsync("系统管理员", "用户删除", $"用户ID={userId}");
            _logger.LogInformation("用户已删除：UserId={UserId}", userId);
            return Results.Ok(new { code = 0, msg = "删除成功" });
        }

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
        {
            _logger.LogWarning("用户删除失败：用户ID {UserId} 不存在", userId);
            return Results.NotFound(new { code = 40402, msg = "用户不存在" });
        }

        _store.Users.RemoveAt(uidx);
        await _cache.DeleteAsync("user:list:v2");
        AddOperationLog("系统管理员", "用户删除", $"用户ID={userId}");
        _logger.LogInformation("内存库用户已删除：UserId={UserId}", userId);
        return Results.Ok(new { code = 0, msg = "删除成功" });
    }

    private void AddOperationLog(string operatorName, string action, string detail)
    {
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: operatorName,
            Action: action,
            Detail: detail,
            CreatedAt: DateTimeOffset.Now));
    }

    private string BuildJwtToken(string userName, string role)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userName),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _jwtIssuer,
            audience: _jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtExpireMinutes),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
