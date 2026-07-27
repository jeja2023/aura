namespace Aura.Api.Product;

internal static class CaseStateMachine
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["new"] = new HashSet<string>(["acknowledged", "false_positive"], StringComparer.OrdinalIgnoreCase),
            ["acknowledged"] = new HashSet<string>(["in_progress", "false_positive"], StringComparer.OrdinalIgnoreCase),
            ["in_progress"] = new HashSet<string>(["paused", "escalated", "resolved"], StringComparer.OrdinalIgnoreCase),
            ["paused"] = new HashSet<string>(["in_progress"], StringComparer.OrdinalIgnoreCase),
            ["escalated"] = new HashSet<string>(["in_progress"], StringComparer.OrdinalIgnoreCase),
            ["resolved"] = new HashSet<string>(["closed"], StringComparer.OrdinalIgnoreCase),
            ["false_positive"] = new HashSet<string>(["closed"], StringComparer.OrdinalIgnoreCase),
            ["closed"] = new HashSet<string>(["reopened"], StringComparer.OrdinalIgnoreCase),
            ["reopened"] = new HashSet<string>(["in_progress"], StringComparer.OrdinalIgnoreCase)
        };

    public static bool TryValidate(
        string? currentStatus,
        string? targetStatus,
        string? reasonCode,
        bool canReview,
        out string normalizedTarget,
        out string? error)
    {
        normalizedTarget = (targetStatus ?? string.Empty).Trim().ToLowerInvariant();
        var current = (currentStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedTransitions.TryGetValue(current, out var targets) || !targets.Contains(normalizedTarget))
        {
            error = $"不允许从 {current} 转换为 {normalizedTarget}";
            return false;
        }

        if ((normalizedTarget is "paused" or "false_positive" or "closed" or "reopened")
            && string.IsNullOrWhiteSpace(reasonCode))
        {
            error = "该状态转换必须填写原因";
            return false;
        }

        if (current == "closed" && normalizedTarget == "reopened" && !canReview)
        {
            error = "关闭后的案件仅允许复核人员重开";
            return false;
        }

        error = null;
        return true;
    }
}

internal static class BusinessEventStateMachine
{
    public static bool TryResolve(string? currentStatus, string? action, string? reasonCode, out string target, out string? error)
    {
        var current = (currentStatus ?? string.Empty).Trim().ToLowerInvariant();
        var command = (action ?? string.Empty).Trim().ToLowerInvariant();
        target = command switch
        {
            "acknowledge" => "acknowledged",
            "dismiss" => "dismissed",
            "reopen" => "open",
            _ => string.Empty
        };

        var allowed = (current, target) switch
        {
            ("open", "acknowledged") => true,
            ("open", "dismissed") => true,
            ("acknowledged", "dismissed") => true,
            ("dismissed", "open") => true,
            _ => false
        };
        if (!allowed)
        {
            error = $"不允许对 {current} 状态执行 {command}";
            return false;
        }

        if ((target is "dismissed" or "open") && string.IsNullOrWhiteSpace(reasonCode))
        {
            error = "驳回或重开必须填写分类原因";
            return false;
        }

        error = null;
        return true;
    }
}

