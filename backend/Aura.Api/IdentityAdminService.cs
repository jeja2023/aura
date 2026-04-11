using System.IdentityModel.Tokens.Jwt;
using Aura.Api.Models;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Internal;
using Microsoft.IdentityModel.Tokens;

namespace Aura.Api;

internal sealed class IdentityAdminService
{
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
            _logger.LogWarning("登录失败：用户名或密码错误。用户：{UserName}", req.UserName);
            return Results.BadRequest(new { code = 40003, msg = "用户名或密码错误" });
        }

        var role = AuraHelpers.ConvertRole(dbUser.RoleName);
        var token = BuildJwtToken(req.UserName, role);
        http.Response.Cookies.Append("aura_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(_jwtExpireMinutes)
        });
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
            var cacheRows = JsonSerializer.Deserialize<List<DbRole>>(cached);
            if (cacheRows is { Count: > 0 })
            {
                return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
            }
        }

        var rows = await _db.GetRolesAsync();
        if (rows.Count > 0)
        {
            await _cache.SetAsync("role:list", JsonSerializer.Serialize(rows), TimeSpan.FromMinutes(5));
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
        var cached = await _cache.GetAsync("user:list");
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var cacheRows = JsonSerializer.Deserialize<List<DbUserListItem>>(cached);
            if (cacheRows is { Count: > 0 })
            {
                return Results.Ok(new { code = 0, msg = "查询成功", data = cacheRows, from = "redis" });
            }
        }

        var rows = await _db.GetUsersAsync();
        if (rows.Count > 0)
        {
            await _cache.SetAsync("user:list", JsonSerializer.Serialize(rows), TimeSpan.FromMinutes(5));
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }

        var mockRows = _store.Users.OrderByDescending(x => x.UserId).ToList();
        return Results.Ok(new { code = 0, msg = "查询成功", data = mockRows });
    }

    public async Task<IResult> CreateUserAsync(UserCreateReq req)
    {
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
        {
            return Results.BadRequest(new { code = 40012, msg = "用户名或密码不能为空" });
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);
        var dbId = await _db.InsertUserAsync(req.UserName, hash, req.RoleId);
        if (dbId.HasValue)
        {
            await _db.InsertOperationAsync("系统管理员", "用户创建", $"用户={req.UserName}, 角色ID={req.RoleId}");
            _logger.LogInformation("用户创建成功：{UserName}", req.UserName);
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { userId = dbId.Value, userName = req.UserName, roleId = req.RoleId, status = 1 } });
        }

        var entity = new UserEntity(
            UserId: Interlocked.Increment(ref _store.UserSeed),
            UserName: req.UserName,
            RoleName: req.RoleId == 1 ? "super_admin" : "building_admin",
            Status: 1,
            CreatedAt: DateTimeOffset.Now);
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
        AddOperationLog("系统管理员", "用户状态更新", $"用户ID={userId}, 状态={req.Status}");
        _logger.LogInformation("内存库用户状态已更新：ID={UserId}, 状态={Status}", userId, req.Status);
        return Results.Ok(new { code = 0, msg = "状态更新成功", data = updated });
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
