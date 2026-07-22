using System.Security.Claims;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.MediaAnalysis;

internal sealed class TenantScopeAccessService(PgSqlConnectionFactory connectionFactory)
{
    public static bool IsSuperAdmin(ClaimsPrincipal user) => user.IsInRole("super_admin");

    public async Task<bool> CanAccessAsync(
        ClaimsPrincipal user,
        long tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId <= 0) return false;
        if (IsSuperAdmin(user)) return true;
        var role = user.FindFirstValue(ClaimTypes.Role)?.Trim();
        if (string.IsNullOrWhiteSpace(role)) return false;
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM tenant_role_scope WHERE tenant_id=@TenantId AND role_name=@RoleName)",
            new { TenantId = tenantId, RoleName = role }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AccessibleTenant>> ListAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var role = user.FindFirstValue(ClaimTypes.Role)?.Trim();
        await using var connection = connectionFactory.CreateConnection();
        var sql = IsSuperAdmin(user)
            ? "SELECT tenant_id AS TenantId,tenant_code AS TenantCode,tenant_name AS TenantName FROM tenant_project WHERE enabled=TRUE ORDER BY tenant_id"
            : """
              SELECT tenant.tenant_id AS TenantId,tenant.tenant_code AS TenantCode,tenant.tenant_name AS TenantName
              FROM tenant_project tenant
              JOIN tenant_role_scope scope ON scope.tenant_id=tenant.tenant_id
              WHERE tenant.enabled=TRUE AND scope.role_name=@RoleName
              ORDER BY tenant.tenant_id
              """;
        return (await connection.QueryAsync<AccessibleTenant>(new CommandDefinition(
            sql, new { RoleName = role }, cancellationToken: cancellationToken))).AsList();
    }
}

internal sealed record AccessibleTenant(long TenantId, string TenantCode, string TenantName);
