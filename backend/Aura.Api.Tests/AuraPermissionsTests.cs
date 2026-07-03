using System.Security.Claims;
using Aura.Api.Internal;
using Xunit;

namespace Aura.Api.Tests;

public sealed class AuraPermissionsTests
{
    [Fact]
    public void 权限Json解析会归一化旧权限名并去重()
    {
        var permissions = AuraPermissions.ParsePermissionJson("""["alert","ALERT.MANAGE","device_diag","media","ai_settings","export","space","report","tenant","ai_platform"]""");

        Assert.Equal(
            [
                AuraPermissions.AlertManage,
                AuraPermissions.DeviceDiagnostics,
                AuraPermissions.AiSettings,
                AuraPermissions.Export,
                AuraPermissions.SpaceManage,
                AuraPermissions.ReportManage,
                AuraPermissions.TenantManage,
                AuraPermissions.AiPlatform
            ],
            permissions);
    }

    [Fact]
    public void 超级管理员即使没有权限声明也可以访问细粒度权限()
    {
        var user = CreatePrincipal("super_admin");

        Assert.True(AuraPermissions.HasPermission(user, AuraPermissions.AiSettings));
    }

    [Fact]
    public void 普通角色必须携带匹配权限声明()
    {
        var user = CreatePrincipal("building_admin", AuraPermissions.Export);

        Assert.True(AuraPermissions.HasPermission(user, AuraPermissions.Export));
        Assert.False(AuraPermissions.HasPermission(user, AuraPermissions.AiSettings));
    }

    [Fact]
    public void All权限声明可以访问任意细粒度权限()
    {
        var user = CreatePrincipal("building_admin", AuraPermissions.All);

        Assert.True(AuraPermissions.HasPermission(user, AuraPermissions.DeviceDiagnostics));
    }

    private static ClaimsPrincipal CreatePrincipal(string role, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        foreach (var permission in permissions)
        {
            claims.Add(new Claim(AuraPermissions.ClaimType, permission));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
