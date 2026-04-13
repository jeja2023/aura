/* 文件：仓库根与统一存储路径 | File: Project root and unified storage path */
using Microsoft.Extensions.Hosting;

namespace Aura.Api.Internal;

/// <summary>
/// 约定：所有上传、导出、抓拍归档等落盘仅使用仓库根目录下的 <c>storage/</c>（URL 前缀 <c>/storage/</c>），勿在 backend 下另建 storage。
/// 配置中的相对路径一律相对「仓库根」解析，勿依赖进程当前工作目录（否则 <c>dotnet run</c> 在 backend/Aura.Api 下会把 <c>storage/...</c> 错解到 backend/Aura.Api/storage）。
/// </summary>
internal static class ProjectPaths
{
    private const string SolutionFileName = "Aura.sln";

    /// <summary>
    /// 解析仓库根：优先从 ContentRoot 向上查找含 <see cref="SolutionFileName"/> 的目录；否则回退为「ContentRoot 的上上级」（典型本地结构 backend/Aura.Api）。
    /// 容器内 ContentRoot 常为 /app 且无解决方案文件时，回退为 ContentRoot（与 /app/storage 挂载一致）。
    /// </summary>
    public static string ResolveProjectRoot(IHostEnvironment env)
    {
        var cr = env.ContentRootPath;
        if (string.IsNullOrWhiteSpace(cr)) return cr;

        try
        {
            var dir = new DirectoryInfo(cr);
            for (var i = 0; i < 12 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            /* 忽略盘符/权限异常，走回退 */
        }

        return Directory.GetParent(cr)?.Parent?.FullName ?? cr;
    }

    /// <summary>
    /// 统一存储根目录：{仓库根}/storage。
    /// </summary>
    public static string ResolveStorageRoot(IHostEnvironment env) =>
        Path.Combine(ResolveProjectRoot(env), "storage");

    /// <summary>
    /// 将配置中的相对路径解析为绝对路径（相对仓库根）。已是绝对路径则规范化。
    /// </summary>
    public static string? ResolvePathRelativeToProjectRoot(IHostEnvironment env, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
        return Path.GetFullPath(Path.Combine(ResolveProjectRoot(env), p));
    }
}
