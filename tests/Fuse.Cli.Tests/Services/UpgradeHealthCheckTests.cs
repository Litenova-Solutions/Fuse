using System.Runtime.CompilerServices;
using Fuse.Cli.Services;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// R33: the upgrade health-check gate. A healthy binary (starts and reports a version) passes and the switch
// proceeds; an unhealthy one fails so the upgrade rolls back to the previous version.
public sealed class UpgradeHealthCheckTests
{
    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public async Task Check_OnRealBuiltBinary_IsHealthy_AndReportsVersion()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var fuseDll = new[] { "Release", "Debug" }
            .Select(configuration => Path.Combine(root!, "src", "Host", "Fuse.Cli", "bin", configuration, "net10.0", "fuse.dll"))
            .FirstOrDefault(File.Exists)
            ?? Path.Combine(root!, "src", "Host", "Fuse.Cli", "bin", "Release", "net10.0", "fuse.dll");
        Assert.True(File.Exists(fuseDll), $"built fuse.dll not found at {fuseDll}; build the solution first");

        var result = await UpgradeHealthCheck.CheckAsync(fuseDll, CancellationToken.None);

        Assert.True(result.Healthy, result.Detail);
        Assert.False(string.IsNullOrWhiteSpace(result.ReportedVersion));
        Assert.False(UpgradeHealthCheck.ShouldRollBack(result));
    }

    [Fact]
    public async Task Check_OnMissingBinary_IsUnhealthy_AndRollsBack()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "fuse-nonexistent", Guid.NewGuid().ToString("N"), "fuse.dll");

        var result = await UpgradeHealthCheck.CheckAsync(bogus, CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.True(UpgradeHealthCheck.ShouldRollBack(result));
    }
}
