using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Aura.Api.Serialization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Aura.Api;

internal sealed class IdentityAdminService
{
    private readonly AppStore _store;
    private readonly UserAuthRepository _userAuthRepository;
    private readonly AuditRepository _auditRepository;
    private readonly RedisCacheService _cache;
    private readonly ILogger<IdentityAdminService> _logger;
    private readonly string _jwtKey;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly int _jwtExpireMinutes;

    public IdentityAdminService(
        AppStore store,
        UserAuthRepository userAuthRepository,
        AuditRepository auditRepository,
        RedisCacheService cache,
        ILogger<IdentityAdminService> logger,
        string jwtKey,
        string jwtIssuer,
        string jwtAudience,
        int jwtExpireMinutes)
    {
        _store = store;
        _userAuthRepository = userAuthRepository;
        _auditRepository = auditRepository;
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
            return AuraApiResults.BadRequest("用户名或密码不能为空", 40001);
        }

        var userName = req.UserName.Trim();
        var dbUser = await _userAuthRepository.FindUserAsync(userName);
        if (dbUser is null || !BCrypt.Net.BCrypt.Verify(req.Password, dbUser.PasswordHash))
        {
            var failIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await _auditRepository.InsertSystemLogAsync("警告", "认证服务", $"登录失败，用户名={userName}, IP={failIp}");
            _logger.LogWarning("登录失败：用户名或密码错误。用户：{UserName}", userName);
            return AuraApiResults.BadRequest("用户名或密码错误", 40003);
        }

        var role = AuraHelpers.ConvertRole(dbUser.RoleName);
        var loginAt = DateTimeOffset.Now;
        if (await _userAuthRepository.UpdateUserLastLoginByUserNameAsync(userName, loginAt))
        {
            await _cache.DeleteAsync("user:list:v2");
        }

        var userIdx = _store.Users.FindIndex(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (userIdx >= 0)
        {
            _store.Users[userIdx] = _store.Users[userIdx] with { LastLoginAt = loginAt };
        }

        var expireAt = DateTimeOffset.UtcNow.AddMinutes(_jwtExpireMinutes);
        var token = BuildJwtToken(userName, role, dbUser.MustChangePassword);
        AppendAuthCookie(http, token, expireAt);

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await _auditRepository.InsertOperationAsync(userName, "用户登录", $"角色={role}, IP={ip}");
        await _auditRepository.InsertSystemLogAsync("信息", "认证服务", $"用户登录成功，用户名={userName}, 角色={role}, IP={ip}");
        _logger.LogInformation("用户登录成功：{UserName}, 角色={Role}", userName, role);

        return Results.Ok(new
        {
            code = 0,
            msg = "登录成功",
            data = new
            {
                expireAt,
                userName,
                role,
                mustChangePassword = dbUser.MustChangePassword
            }
        });
    }

    public IResult Logout(HttpContext http)
    {
        var userName = http.User?.Identity?.Name;
        var operatorName = string.IsNullOrWhiteSpace(userName) ? "匿名用户" : userName;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _ = _auditRepository.InsertOperationAsync(operatorName, "用户退出", $"IP={ip}");
        _ = _auditRepository.InsertSystemLogAsync("信息", "认证服务", $"用户退出登录，用户名={operatorName}, IP={ip}");

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

        var rows = await _userAuthRepository.GetRolesAsync();
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
            return AuraApiResults.BadRequest("角色名不能为空", 40011);
        }

        var permissionJson = req.PermissionJson ?? "[]";
        var dbId = await _userAuthRepository.InsertRoleAsync(req.RoleName, permissionJson);
        if (dbId.HasValue)
        {
            await _auditRepository.InsertOperationAsync("系统管理员", "角色创建", $"角色={req.RoleName}");
            _logger.LogInformation("角色创建成功：{RoleName}", req.RoleName);
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { roleId = dbId.Value, roleName = req.RoleName, permissionJson } });
        }

        var entity = new RoleEntity(Interlocked.Increment(ref _store.RoleSeed), req.RoleName, permissionJson);
        _store.Roles.Add(entity);
        AddOperationLog("系统管理员", "角色创建", $"角色={req.RoleName}");
        _logger.LogWarning("数据库创建角色失败，已存入内存库：{RoleName}", req.RoleName);
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> CreateUserAsync(UserCreateReq req)
    {
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
        {
            return AuraApiResults.BadRequest("用户名或密码不能为空", 40012);
        }

        var userName = req.UserName.Trim();
        var displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? userName : req.DisplayName.Trim();
        if (displayName.Length > 64)
        {
            return AuraApiResults.BadRequest("显示名称过长", 40018);
        }

        var passwordError = ValidatePassword(req.Password);
        if (passwordError is not null)
        {
            return AuraApiResults.BadRequest(passwordError, 40019);
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);
        var dbId = await _userAuthRepository.InsertUserAsync(userName, displayName, hash, req.RoleId);
        if (dbId.HasValue)
        {
            await _cache.DeleteAsync("user:list:v2");
            await _auditRepository.InsertOperationAsync("系统管理员", "用户创建", $"用户={userName}, 角色ID={req.RoleId}");
            _logger.LogInformation("用户创建成功：{UserName}", userName);
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { userId = dbId.Value, userName, displayName, roleId = req.RoleId, status = 1, mustChangePassword = false } });
        }

        var entity = new UserEntity(
            UserId: Interlocked.Increment(ref _store.UserSeed),
            UserName: userName,
            DisplayName: displayName,
            RoleName: req.RoleId == 1 ? "super_admin" : "building_admin",
            RoleId: req.RoleId,
            Status: 1,
            CreatedAt: DateTimeOffset.Now,
            LastLoginAt: null,
            MustChangePassword: false);
        _store.Users.Add(entity);
        AddOperationLog("系统管理员", "用户创建", $"用户={userName}, 角色ID={req.RoleId}");
        _logger.LogWarning("数据库创建用户失败，已存入内存库：{UserName}", userName);
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> UpdateUserStatusAsync(long userId, UserStatusReq req)
    {
        var ok = await _userAuthRepository.UpdateUserStatusAsync(userId, req.Status);
        if (ok)
        {
            await _cache.DeleteAsync("user:list:v2");
            await _auditRepository.InsertOperationAsync("系统管理员", "用户状态更新", $"用户ID={userId}, 状态={req.Status}");
            _logger.LogInformation("用户状态更新成功：ID={UserId}, 状态={Status}", userId, req.Status);
            return Results.Ok(new { code = 0, msg = "状态更新成功" });
        }

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
        {
            _logger.LogWarning("用户状态更新失败：用户ID {UserId} 不存在", userId);
            return AuraApiResults.NotFound("用户不存在", 40402);
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
            return AuraApiResults.BadRequest("用户名不能为空", 40013);

        var userName = req.UserName.Trim();
        var displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? userName : req.DisplayName.Trim();
        if (userName.Length > 64)
            return AuraApiResults.BadRequest("用户名过长", 40016);
        if (displayName.Length > 64)
            return AuraApiResults.BadRequest("显示名称过长", 40018);
        if (req.RoleId != 1 && req.RoleId != 2)
            return AuraApiResults.BadRequest("角色无效", 40014);
        if (req.Status != 0 && req.Status != 1)
            return AuraApiResults.BadRequest("状态无效", 40015);

        var roleName = req.RoleId == 1 ? "super_admin" : "building_admin";

        try
        {
            var affected = await _userAuthRepository.UpdateUserProfileAsync(userId, userName, displayName, req.RoleId, req.Status);
            if (affected > 0)
            {
                await _cache.DeleteAsync("user:list:v2");
                await _auditRepository.InsertOperationAsync("系统管理员", "用户资料更新", $"用户ID={userId}, 用户名={userName}, 显示名称={displayName}, 角色ID={req.RoleId}, 状态={req.Status}");
                _logger.LogInformation("用户资料已更新：UserId={UserId}", userId);
                return Results.Ok(new { code = 0, msg = "保存成功" });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return AuraApiResults.Conflict("用户名已被占用", 40901);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库更新用户资料异常。UserId={UserId}", userId);
            return AuraApiResults.InternalServerError("保存失败");
        }

        if (_store.Users.Any(u => u.UserId != userId && string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)))
            return AuraApiResults.Conflict("用户名已被占用", 40901);

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
            return AuraApiResults.NotFound("用户不存在", 40402);

        var prev = _store.Users[uidx];
        _store.Users[uidx] = prev with
        {
            UserName = userName,
            DisplayName = displayName,
            RoleName = roleName,
            RoleId = req.RoleId,
            Status = req.Status
        };
        await _cache.DeleteAsync("user:list:v2");
        AddOperationLog("系统管理员", "用户资料更新", $"用户ID={userId}, 用户名={userName}, 显示名称={displayName}, 角色ID={req.RoleId}, 状态={req.Status}");
        return Results.Ok(new { code = 0, msg = "保存成功", data = _store.Users[uidx] });
    }

    public async Task<IResult> ResetUserPasswordAsync(long userId, UserPasswordResetReq req)
    {
        var hasExplicitPassword = !string.IsNullOrWhiteSpace(req.NewPassword);
        var nextPassword = hasExplicitPassword ? req.NewPassword!.Trim() : GenerateTemporaryPassword();
        var passwordError = ValidatePassword(nextPassword);
        if (passwordError is not null)
        {
            return AuraApiResults.BadRequest(passwordError, 40019);
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(nextPassword);
        if (await _userAuthRepository.UpdateUserPasswordByUserIdAsync(userId, hash, mustChangePassword: true))
        {
            await _cache.DeleteAsync("user:list:v2");
            await _auditRepository.InsertOperationAsync("系统管理员", "用户密码重置", $"用户ID={userId}");
            _logger.LogInformation("用户密码已重置：UserId={UserId}", userId);
            return Results.Ok(new
            {
                code = 0,
                msg = hasExplicitPassword ? "密码已重置，用户下次登录需先修改密码" : "已生成临时密码，用户下次登录需先修改密码",
                data = new
                {
                    mustChangePassword = true,
                    temporaryPassword = hasExplicitPassword ? null : nextPassword
                }
            });
        }

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
            return AuraApiResults.NotFound("用户不存在", 40402);

        _store.Users[uidx] = _store.Users[uidx] with { MustChangePassword = true };
        await _cache.DeleteAsync("user:list:v2");
        _logger.LogWarning("内存库用户未存储密码哈希，跳过持久化重置：UserId={UserId}", userId);
        return Results.Ok(new
        {
            code = 0,
            msg = hasExplicitPassword ? "密码已重置（内存模式），用户下次登录需先修改密码" : "已生成临时密码（内存模式），用户下次登录需先修改密码",
            data = new
            {
                mustChangePassword = true,
                temporaryPassword = hasExplicitPassword ? null : nextPassword
            }
        });
    }

    public async Task<IResult> ChangePasswordAsync(HttpContext http, ChangePasswordReq req)
    {
        var userName = http.User?.Identity?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return AuraApiResults.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(req.CurrentPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
        {
            return AuraApiResults.BadRequest("当前密码与新密码不能为空", 40020);
        }

        if (string.Equals(req.CurrentPassword, req.NewPassword, StringComparison.Ordinal))
        {
            return AuraApiResults.BadRequest("新密码不能与当前密码相同", 40022);
        }

        var passwordError = ValidatePassword(req.NewPassword);
        if (passwordError is not null)
        {
            return AuraApiResults.BadRequest(passwordError, 40019);
        }

        var dbUser = await _userAuthRepository.FindUserAsync(userName);
        if (dbUser is null || !BCrypt.Net.BCrypt.Verify(req.CurrentPassword, dbUser.PasswordHash))
        {
            return AuraApiResults.BadRequest("当前密码不正确", 40023);
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        if (!await _userAuthRepository.UpdateUserPasswordByUserNameAsync(userName, hash, mustChangePassword: false))
        {
            return AuraApiResults.InternalServerError("修改密码失败");
        }

        var userIdx = _store.Users.FindIndex(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (userIdx >= 0)
        {
            _store.Users[userIdx] = _store.Users[userIdx] with { MustChangePassword = false };
        }

        await _cache.DeleteAsync("user:list:v2");

        var role = AuraHelpers.ConvertRole(dbUser.RoleName);
        var expireAt = DateTimeOffset.UtcNow.AddMinutes(_jwtExpireMinutes);
        var token = BuildJwtToken(userName, role, mustChangePassword: false);
        AppendAuthCookie(http, token, expireAt);

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await _auditRepository.InsertOperationAsync(userName, "修改自己的密码", $"IP={ip}");
        await _auditRepository.InsertSystemLogAsync("信息", "认证服务", $"用户修改密码成功，用户名={userName}, IP={ip}");

        return Results.Ok(new
        {
            code = 0,
            msg = "密码修改成功",
            data = new
            {
                userName,
                role,
                expireAt,
                mustChangePassword = false
            }
        });
    }

    public async Task<IResult> DeleteUserAsync(long userId)
    {
        var ok = await _userAuthRepository.DeleteUserAsync(userId);
        if (ok)
        {
            await _cache.DeleteAsync("user:list:v2");
            await _auditRepository.InsertOperationAsync("系统管理员", "用户删除", $"用户ID={userId}");
            _logger.LogInformation("用户已删除：UserId={UserId}", userId);
            return Results.Ok(new { code = 0, msg = "删除成功" });
        }

        var uidx = _store.Users.FindIndex(x => x.UserId == userId);
        if (uidx < 0)
        {
            _logger.LogWarning("用户删除失败：用户ID {UserId} 不存在", userId);
            return AuraApiResults.NotFound("用户不存在", 40402);
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

    private static string? ValidatePassword(string password)
    {
        var value = password?.Trim() ?? string.Empty;
        if (value.Length < 12)
        {
            return "密码长度不能少于 12 位";
        }

        if (value.Length > 128)
        {
            return "密码长度不能超过 128 位";
        }

        if (!value.Any(char.IsUpper))
        {
            return "密码需至少包含 1 个大写字母";
        }

        if (!value.Any(char.IsLower))
        {
            return "密码需至少包含 1 个小写字母";
        }

        if (!value.Any(char.IsDigit))
        {
            return "密码需至少包含 1 个数字";
        }

        if (!value.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            return "密码需至少包含 1 个特殊字符";
        }

        return null;
    }

    private static string GenerateTemporaryPassword()
    {
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

    private static void AppendAuthCookie(HttpContext http, string token, DateTimeOffset expireAt)
    {
        http.Response.Cookies.Append("aura_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expireAt
        });
    }

    private string BuildJwtToken(string userName, string role, bool mustChangePassword)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userName),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Role, role),
            new(AuraHelpers.MustChangePasswordClaimType, mustChangePassword ? "true" : "false"),
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
