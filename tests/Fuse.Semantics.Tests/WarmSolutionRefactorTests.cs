using System.Runtime.CompilerServices;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R42: over a real MSBuild load of the SampleShop fixture, a refactor through the warm-solution cache produces a
// byte-identical diff whether the solution is loaded cold (first call) or reused warm (second call), and the
// second call does not reload. Doctor's warm snapshot reports the same per-project tier a fresh load reports.
// Tolerant: if the fixture solution does not load in this environment the test asserts a clean abstention/skip
// rather than failing, matching the other refactorer integration tests.
public sealed class WarmSolutionRefactorTests
{
    private static string? SampleShopSolution([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        var sln = Path.Combine(dir.FullName, "tests", "fixtures", "SampleShop", "SampleShop.sln");
        return File.Exists(sln) ? sln : null;
    }

    [Fact]
    [Trait("Category", "RequiresSdk")]
    public async Task Warm_rename_is_byte_identical_to_cold_and_skips_the_reload()
    {
        var sln = SampleShopSolution();
        if (sln is null)
            return; // Fixture not present.

        using var cache = new WarmSolutionCache(cap: 3);
        var refactorer = new RenameRefactorer(cache);

        var cold = await refactorer.RenameAsync(sln, "SecretsHolder", "SecretsBag", CancellationToken.None);
        if (!cold.Renamed)
        {
            Assert.False(string.IsNullOrEmpty(cold.Reason)); // A clean abstention when the solution did not load.
            return;
        }

        Assert.Equal(1, cache.LoadCount); // The first call loaded once.
        var warm = await refactorer.RenameAsync(sln, "SecretsHolder", "SecretsBag", CancellationToken.None);

        Assert.Equal(1, cache.LoadCount); // The second call reused the held solution: no reload.
        Assert.True(warm.Renamed);
        // Byte-identical staged diff between the cold and the warm path.
        Assert.Equal(cold.Diffs.Count, warm.Diffs.Count);
        for (var i = 0; i < cold.Diffs.Count; i++)
        {
            Assert.Equal(cold.Diffs[i].FilePath, warm.Diffs[i].FilePath);
            Assert.Equal(cold.Diffs[i].UnifiedDiff, warm.Diffs[i].UnifiedDiff);
        }
    }

    [Fact]
    [Trait("Category", "RequiresSdk")]
    public async Task Doctor_warm_snapshot_matches_a_fresh_load()
    {
        var sln = SampleShopSolution();
        if (sln is null)
            return;

        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(
            Path.GetDirectoryName(sln)!, CancellationToken.None);
        var fresh = await new RoslynWorkspaceLoader().LoadAsync(discovery, CancellationToken.None);
        if (!fresh.SemanticLoadSucceeded)
            return; // The environment cannot load the fixture semantically; nothing to compare.

        using var cache = new WarmSolutionCache(cap: 3);
        var cached = await cache.OpenAsync(sln, CancellationToken.None);
        var warm = await RoslynWorkspaceLoader.SnapshotFromSolutionAsync(cached.Solution, cached.LoadFailures, CancellationToken.None);

        Assert.Equal(fresh.SemanticLoadSucceeded, warm.SemanticLoadSucceeded);
        Assert.Equal(fresh.ProjectReports.Count, warm.ProjectReports.Count);
        foreach (var freshReport in fresh.ProjectReports)
        {
            var warmReport = warm.ProjectReports.Single(r => r.Name == freshReport.Name);
            Assert.Equal(freshReport.Loaded, warmReport.Loaded);
            Assert.Equal(freshReport.Reason, warmReport.Reason);
        }
    }
}
