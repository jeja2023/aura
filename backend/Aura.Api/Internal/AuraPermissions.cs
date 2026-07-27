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
    internal const string MediaAnalysisView = "media.analysis.view";
    internal const string MediaAnalysisManage = "media.analysis.manage";
    internal const string MediaAnalysisOperate = "media.analysis.operate";
    internal const string MediaAnalysisReplay = "media.analysis.replay";
    internal const string VectorIndexManage = "vector.index.manage";
    internal const string GraphView = "graph.view";
    internal const string GraphAdmin = "graph.admin";
    internal const string EventView = "event.view";
    internal const string EventManage = "event.manage";
    internal const string CaseView = "case.view";
    internal const string CaseManage = "case.manage";
    internal const string CaseReview = "case.review";
    internal const string InvestigationView = "investigation.view";
    internal const string InvestigationManage = "investigation.manage";
    internal const string EvidenceViewOriginal = "evidence.view_original";
    internal const string EvidenceExport = "evidence.export";
    internal const string EvidenceLegalHold = "evidence.legal_hold";
    internal const string RuleView = "rule.view";
    internal const string RuleManage = "rule.manage";
    internal const string RuleApprove = "rule.approve";
    internal const string AiGovernanceView = "ai.governance.view";
    internal const string AiGovernanceManage = "ai.governance.manage";
    internal const string AiReleaseApprove = "ai.release.approve";
    internal const string IntegrationView = "integration.view";
    internal const string IntegrationManage = "integration.manage";
    internal const string IntegrationTest = "integration.test";
    internal const string OpsView = "ops.view";
    internal const string OpsExecute = "ops.execute";
    internal const string OpsHighImpact = "ops.high_impact";
    internal const string UsageView = "usage.view";
    internal const string UsageManage = "usage.manage";
    internal const string DataGovernanceView = "data.governance.view";
    internal const string DataGovernanceManage = "data.governance.manage";

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
            "media_analysis_view" => MediaAnalysisView,
            "media_analysis_manage" => MediaAnalysisManage,
            "media_analysis_operate" => MediaAnalysisOperate,
            "media_analysis_replay" => MediaAnalysisReplay,
            "vector_index_manage" => VectorIndexManage,
            "graph_view" => GraphView,
            "graph_admin" => GraphAdmin,
            "all" => All,
            var other => other
        };
    }
}
