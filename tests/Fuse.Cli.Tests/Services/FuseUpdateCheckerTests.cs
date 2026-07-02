using Fuse.Cli.Services;

namespace Fuse.Cli.Tests.Services;

// The update checker: version comparison, latest-stable selection, flat-container parsing, and the cache
// round-trip that drives the notice.
public sealed class FuseUpdateCheckerTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "fuse-update-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("3.1.1", "3.1.2", true)]
    [InlineData("3.1.2", "3.1.2", false)]
    [InlineData("3.1.9", "3.2.0", true)]
    [InlineData("3.2.0", "3.1.9", false)]
    [InlineData("3.1.1", "3.1.1+abc123", false)] // same core, different build metadata
    [InlineData("3.1.1", "3.1.2-beta", true)]    // core 3.1.2 still newer than 3.1.1
    [InlineData("3.1.1", "not-a-version", false)]
    public void IsNewer_ComparesNumericCore(string current, string latest, bool expected) =>
        Assert.Equal(expected, FuseUpdateChecker.IsNewer(current, latest));

    [Fact]
    public void SelectLatestStable_PicksHighestAndSkipsPrereleases()
    {
        var versions = new[] { "2.4.0", "3.1.0", "3.1.2", "3.2.0-beta", "3.1.1" };

        Assert.Equal("3.1.2", FuseUpdateChecker.SelectLatestStable(versions));
    }

    [Fact]
    public void SelectLatestStable_AllPrereleaseOrGarbage_ReturnsNull()
    {
        Assert.Null(FuseUpdateChecker.SelectLatestStable(["3.2.0-rc1", "junk"]));
    }

    [Fact]
    public void ParseVersions_ReadsFlatContainerBody()
    {
        var json = """{"versions":["2.0.0","3.1.0","3.1.1"]}""";

        Assert.Equal(["2.0.0", "3.1.0", "3.1.1"], FuseUpdateChecker.ParseVersions(json));
    }

    [Fact]
    public void ParseVersions_MalformedBody_ReturnsEmpty() =>
        Assert.Empty(FuseUpdateChecker.ParseVersions("not json"));

    [Fact]
    public void GetCachedStatus_NoCache_ReturnsNull()
    {
        var checker = new FuseUpdateChecker(_cacheDir);

        Assert.Null(checker.GetCachedStatus("3.1.1"));
    }

    [Fact]
    public void SaveThenGetCachedStatus_ReportsUpdateAvailable()
    {
        var checker = new FuseUpdateChecker(_cacheDir);
        checker.SaveLatest("3.9.0");

        var status = checker.GetCachedStatus("3.1.1");

        Assert.NotNull(status);
        Assert.Equal("3.9.0", status!.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.True(checker.IsCacheFresh());
    }

    [Fact]
    public void CachedStatus_WhenCurrentIsLatest_NoUpdate()
    {
        var checker = new FuseUpdateChecker(_cacheDir);
        checker.SaveLatest("3.1.1");

        Assert.False(checker.GetCachedStatus("3.1.1")!.UpdateAvailable);
    }

    [Fact]
    public void IsEnabled_RespectsDisableEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable(FuseUpdateChecker.DisableEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FuseUpdateChecker.DisableEnvironmentVariable, "0");
            Assert.False(FuseUpdateChecker.IsEnabled);
            Environment.SetEnvironmentVariable(FuseUpdateChecker.DisableEnvironmentVariable, null);
            Assert.True(FuseUpdateChecker.IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FuseUpdateChecker.DisableEnvironmentVariable, original);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
