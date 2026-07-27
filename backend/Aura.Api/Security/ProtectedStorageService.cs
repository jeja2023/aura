using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Microsoft.AspNetCore.StaticFiles;

namespace Aura.Api.Security;

internal sealed class ProtectedStorageService(
    IHostEnvironment environment,
    TenantScopeAccessService tenantAccess,
    ILogger<ProtectedStorageService> logger)
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly string storageRoot = Path.GetFullPath(ProjectPaths.ResolveStorageRoot(environment));

    public async Task<IResult> DownloadAsync(
        HttpContext http,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        var segments = NormalizeSegments(relativePath);
        if (segments is null || segments.Length < 2)
        {
            return AuraApiResults.NotFound("文件不存在", 40440);
        }

        if (!await CanAccessAsync(http, segments, cancellationToken))
        {
            return AuraApiResults.Forbidden("无权访问该文件", 40340);
        }

        var fullPath = ResolvePath(segments);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return AuraApiResults.NotFound("文件不存在", 40440);
        }

        if (!ContentTypes.TryGetContentType(fullPath, out var contentType))
        {
            logger.LogWarning("拒绝提供未知文件类型。path={Path}", string.Join('/', segments));
            return AuraApiResults.NotFound("文件不存在", 40440);
        }

        http.Response.Headers.CacheControl = "private, no-store";
        var downloadName = segments[0].Equals("outputs", StringComparison.OrdinalIgnoreCase) || !CanRenderInline(contentType)
            ? Path.GetFileName(fullPath)
            : null;
        return Results.File(fullPath, contentType, downloadName, enableRangeProcessing: true);
    }

    private async Task<bool> CanAccessAsync(
        HttpContext http,
        string[] segments,
        CancellationToken cancellationToken)
    {
        var topLevel = segments[0].ToLowerInvariant();
        switch (topLevel)
        {
            case "evidence-exports":
            case "logs":
            case "runtime":
                return false;
            case "captures":
                return http.User.IsInRole("building_admin") || http.User.IsInRole("super_admin");
            case "outputs":
                return AuraPermissions.HasPermission(http.User, AuraPermissions.Export);
            case "uploads":
                return segments[1].ToLowerInvariant() switch
                {
                    "floors" or "capture" => http.User.IsInRole("building_admin") || http.User.IsInRole("super_admin"),
                    _ => false
                };
            case "media-analysis":
                return AuraPermissions.HasPermission(http.User, AuraPermissions.MediaAnalysisView)
                    && TryReadTenantId(segments, out var mediaTenantId)
                    && await tenantAccess.CanAccessAsync(http.User, mediaTenantId, cancellationToken);
            case "mobile-evidence":
                return AuraPermissions.HasPermission(http.User, AuraPermissions.EvidenceViewOriginal)
                    && TryReadTenantId(segments, out var evidenceTenantId)
                    && await tenantAccess.CanAccessAsync(http.User, evidenceTenantId, cancellationToken);
            default:
                return false;
        }
    }

    private string? ResolvePath(string[] segments)
    {
        var candidate = Path.GetFullPath(Path.Combine([storageRoot, .. segments]));
        var rootPrefix = storageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static string[]? NormalizeSegments(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.IndexOf('\0') >= 0)
        {
            return null;
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':'))
            ? null
            : segments;
    }

    private static bool TryReadTenantId(string[] segments, out long tenantId)
    {
        tenantId = 0;
        return segments.Length >= 3 && long.TryParse(segments[1], out tenantId) && tenantId > 0;
    }

    private static bool CanRenderInline(string contentType)
        => (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
           || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}
