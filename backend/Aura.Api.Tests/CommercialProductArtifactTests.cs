using System.Text;
using Xunit;

namespace Aura.Api.Tests;

public sealed class CommercialProductArtifactTests
{
    [Fact]
    public void BootstrapExecutesMigrationsNewerThanTheConsolidatedBaseline()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "backend", "Aura.DbMigrator", "Program.cs"), Encoding.UTF8);

        Assert.Contains("ConsolidatedBaselineMigration = 24", source, StringComparison.Ordinal);
        Assert.Contains("Applying post-baseline migration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbenchServiceWorkerNeverCachesApiOrStorageResponses()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "frontend", "workbench", "sw.js"), Encoding.UTF8);

        Assert.Contains("url.pathname.startsWith(\"/api/\")", source, StringComparison.Ordinal);
        Assert.Contains("url.pathname.startsWith(\"/storage/\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("caches.put(request", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbenchContainsTheMobileAndControlledQueryCompletionFlows()
    {
        var root = FindRepoRoot();
        var html = File.ReadAllText(Path.Combine(root, "frontend", "workbench", "workbench.html"), Encoding.UTF8);
        var script = File.ReadAllText(Path.Combine(root, "frontend", "workbench", "workbench.js"), Encoding.UTF8);
        var worker = File.ReadAllText(Path.Combine(root, "frontend", "workbench", "sw.js"), Encoding.UTF8);

        Assert.Contains("id=\"loadMyTasks\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"controlledPlan\"", html, StringComparison.Ordinal);
        Assert.Contains("capture=\"environment\"", html, StringComparison.Ordinal);
        Assert.Contains("/controlled-queries/${queryPlanId}/plan", script, StringComparison.Ordinal);
        Assert.Contains("/mobile/cases/${caseId}/photos", script, StringComparison.Ordinal);
        Assert.Contains("pushManager.subscribe", script, StringComparison.Ordinal);
        Assert.Contains("self.addEventListener(\"push\"", worker, StringComparison.Ordinal);
        Assert.Contains("self.addEventListener(\"notificationclick\"", worker, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Aura.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Aura repository root was not found");
    }
}
