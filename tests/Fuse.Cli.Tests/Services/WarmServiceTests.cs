using System.Runtime.CompilerServices;
using Fuse.Cli.Services;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// R40: the opt-in always-on warm service. Its guardrails - a hard LRU cap, battery/load-aware pause, idle-evict,
// clean uninstall - are enforced here, and it is never installed by `fuse mcp install`.
public sealed class WarmServiceTests
{
    [Fact]
    public void Lru_EnforcesHardCap_EvictingLeastRecentlyUsed()
    {
        var lru = new WarmServiceLru(cap: 3);
        lru.Touch("/r/a");
        lru.Touch("/r/b");
        lru.Touch("/r/c");
        var evicted = lru.Touch("/r/d"); // exceeds the cap of 3.

        Assert.Equal(3, lru.Repos.Count);
        Assert.Contains(evicted, e => e.Replace('\\', '/').EndsWith("/r/a", StringComparison.OrdinalIgnoreCase)); // LRU evicted.
        Assert.DoesNotContain(lru.Repos, r => r.Replace('\\', '/').EndsWith("/r/a", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Lru_Touch_MovesToMostRecent_NoDuplicate()
    {
        var lru = new WarmServiceLru(cap: 2);
        lru.Touch("/r/a");
        lru.Touch("/r/b");
        var evicted = lru.Touch("/r/a"); // re-touch a: it becomes most-recent, nothing evicted.

        Assert.Empty(evicted);
        Assert.Equal(2, lru.Repos.Count);
        Assert.EndsWith("/r/a", lru.Repos[0].Replace('\\', '/'), StringComparison.OrdinalIgnoreCase); // most recent.
    }

    [Fact]
    public void Policy_Pauses_OnBatteryOrHighLoad()
    {
        Assert.True(WarmServicePolicy.ShouldPause(onBattery: true, highLoad: false));
        Assert.True(WarmServicePolicy.ShouldPause(onBattery: false, highLoad: true));
        Assert.False(WarmServicePolicy.ShouldPause(onBattery: false, highLoad: false));
    }

    [Fact]
    public void Policy_Evicts_PastIdleWindow()
    {
        Assert.True(WarmServicePolicy.ShouldEvict(TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30)));
        Assert.False(WarmServicePolicy.ShouldEvict(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Definition_ProducesInstallUninstallAndNotice()
    {
        Assert.False(string.IsNullOrWhiteSpace(WarmServiceDefinition.InstallCommand("/usr/local/bin/fuse")));
        Assert.False(string.IsNullOrWhiteSpace(WarmServiceDefinition.UninstallCommand()));
        Assert.Contains(WarmService.ServiceName, WarmServiceDefinition.UninstallCommand(), StringComparison.Ordinal);
        Assert.Contains("uninstall", WarmServiceDefinition.FirstRunNotice(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void McpInstall_NeverInstallsTheWarmService()
    {
        // R40 guardrail: `fuse mcp install` must never install the always-on service; it stays opt-in via
        // `fuse warm --service install`. This source guard catches an accidental coupling.
        var root = RepoRoot();
        Assert.NotNull(root);
        var mcpInstall = File.ReadAllText(Path.Combine(root!, "src", "Host", "Fuse.Cli", "Services", "McpInstallService.cs"));
        Assert.DoesNotContain("WarmService", mcpInstall, StringComparison.Ordinal);
        Assert.DoesNotContain("--service install", mcpInstall, StringComparison.Ordinal);
    }

    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
