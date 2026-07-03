using System.Security.Claims;
using System.Text.Json;

namespace Aura.Api.Internal;

internal static class AuraPermissions
{
    internal const string ClaimType = "aura:permission";
    internal const string All = "all";
    internal const string AlertManage = "alert.manage";
    internal const string AiSettings = "ai.settings";
    internal const string DeviceDiagnostics = "device.diag";
    internal const string Export = "export";
    internal const string ReportManage = "report.manage";
    internal const string SpaceManage = "space.manage";
    internal const string TenantManage = "tenant.manage";
    internal const string AiPlatform = "ai.platform";

    public static IReadOnlyList<string> ParsePermissionJson(string? permissionJson)
    {
        if (string.IsNullOrWhiteSpace(permissionJson))
        {
            return [];
        }

        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(permissionJson);
            return Normalize(values ?? []);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = NormalizePermission(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public static bool HasPermission(ClaimsPrincipal user, string permission)
    {
        if (user.IsInRole("super_admin"))
        {
            return true;
        }

        var required = NormalizePermission(permission);
        if (string.IsNullOrWhiteSpace(required))
        {
            return false;
        }

        return user.Claims
            .Where(claim => claim.Type == ClaimType)
            .Select(claim => NormalizePermission(claim.Value))
            .Any(value => string.Equals(value, All, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, required, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizePermission(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "alert" => AlertManage,
            "ai" or "ai_settings" => AiSettings,
            "device_diag" or "device.diagnostics" or "media" => DeviceDiagnostics,
            "export" => Export,
            "report" or "reports" => ReportManage,
            "space" => SpaceManage,
            "tenant" or "tenants" => TenantManage,
            "ai_platform" or "ai.platform" => AiPlatform,
            "all" => All,
            var other => other
        };
    }
}
