/* 文件：与 Testing 配置对齐的 JWT 签发（集成测试） | File: Test JWT aligned with appsettings.Testing.json */
/*
 * 【重要 · 维护提示】
 * 修改 backend/Aura.Api/appsettings.Testing.json 内 Jwt:Key、Jwt:Issuer、Jwt:Audience 时，
 * 必须同步修改本文件中的 Key、Issuer、Audience 常量；否则「已登录携带令牌访问根路径」等用例会失败。
 */
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Aura.Api.Integration.Tests;

/// <summary>
/// 签名参数须与 <c>backend/Aura.Api/appsettings.Testing.json</c> 的 Jwt 段一致（见文件头注释）。
/// </summary>
internal static class TestingJwt
{
    // 与 appsettings.Testing.json → Jwt 节点保持逐字一致
    internal const string Key = "aura-integration-test-jwt-signing-key-min-32-chars";
    internal const string Issuer = "Aura.Api.Testing";
    internal const string Audience = "Aura.Client.Testing";

    internal static string CreateToken(string userName = "integration_tester", string role = "super_admin")
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userName),
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.Role, role)
        };
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
