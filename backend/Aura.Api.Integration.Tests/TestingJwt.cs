/* 文件：与 Testing 配置对齐的 JWT 签发（集成测试） | File: Test JWT aligned with appsettings.Testing.json */
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Aura.Api.Internal;
using Microsoft.IdentityModel.Tokens;

namespace Aura.Api.Integration.Tests;

internal static class TestingJwt
{
    internal const string Key = "aura-integration-test-jwt-signing-key-min-32-chars";
    internal const string Issuer = "Aura.Api.Testing";
    internal const string Audience = "Aura.Client.Testing";

    internal static string CreateToken(string userName = "integration_tester", string role = "super_admin", bool mustChangePassword = false)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userName),
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.Role, role),
            new Claim(AuraHelpers.MustChangePasswordClaimType, mustChangePassword ? "true" : "false")
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
