using System.Runtime.CompilerServices;
using Xunit;

namespace Fuse.Cli.Tests;

// R50: the self-contained runtime publish profiles enable ReadyToRun (crossgen) so a one-shot CLI invocation pays
// less JIT warmup on cold start. This guards that the profiles keep PublishReadyToRun=true, so it cannot be silently
// dropped (the packaged framework-dependent dotnet tool has no RID and cannot be R2R'd; the win-x64/linux-x64/etc.
// self-contained binaries can, and are what the release archives and installer ship).
public sealed class ReadyToRunPublishProfileTests
{
    private static string ProfilesDir([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Host", "Fuse.Cli", "Properties", "PublishProfiles");
    }

    [Theory]
    [InlineData("runtime-win-x64.pubxml")]
    [InlineData("runtime-linux-x64.pubxml")]
    [InlineData("runtime-win-arm64.pubxml")]
    [InlineData("runtime-linux-arm64.pubxml")]
    [InlineData("runtime-osx-x64.pubxml")]
    [InlineData("runtime-osx-arm64.pubxml")]
    public void Runtime_profile_enables_ready_to_run_and_self_contained(string profile)
    {
        var path = Path.Combine(ProfilesDir(), profile);
        Assert.True(File.Exists(path), $"publish profile missing: {profile}");
        var content = File.ReadAllText(path);
        Assert.Contains("<PublishReadyToRun>true</PublishReadyToRun>", content);
        Assert.Contains("<SelfContained>true</SelfContained>", content);
        Assert.Contains("<RuntimeIdentifier>", content);
    }
}
