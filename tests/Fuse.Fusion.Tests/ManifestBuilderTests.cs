using Fuse.Analysis.Git;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;

namespace Fuse.Fusion.Tests;

public sealed class ManifestBuilderTests
{
    [Fact]
    public void Build_IncludesFileTokenCounts()
    {
        var files = new[]
        {
            new FileTokenInfo("src/A.cs", 1200),
            new FileTokenInfo("src/B.cs", 400),
        };

        var manifest = ManifestBuilder.Build(files, OutputFormat.Xml);

        Assert.Contains("fuse:manifest", manifest);
        Assert.Contains("src/A.cs (~1.2k tokens)", manifest);
        Assert.Contains("src/B.cs (~400 tokens)", manifest);
    }

    [Fact]
    public void Build_WithGitStats_IncludesChurnAndLastModified()
    {
        var files = new[] { new FileTokenInfo("Program.cs", 100) };
        var gitStats = new GitStatsResult(
            true,
            new Dictionary<string, GitFileStats>
            {
                ["Program.cs"] = new("Program.cs", 7, new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            });

        var manifest = ManifestBuilder.Build(files, OutputFormat.Xml, gitStats);

        Assert.Contains("[commits:7 last:2024-06-01]", manifest);
    }

    [Fact]
    public void Build_GitStatsUnavailable_ShowsGracefulMessage()
    {
        var files = new[] { new FileTokenInfo("Program.cs", 100) };
        var gitStats = new GitStatsResult(false, new Dictionary<string, GitFileStats>());

        var manifest = ManifestBuilder.Build(files, OutputFormat.Xml, gitStats);

        Assert.Contains("git: unavailable", manifest);
    }
}
