using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// C2: the capture-bundle manifest. It round-trips through JSON with its fields intact, and its compatibility check
// mirrors the index's version contract: a bundle from an incompatible Fuse major.minor, or an unknown bundle
// format, is refused with an actionable reason so it is never rehydrated into a wrong-shaped store.
public sealed class CaptureManifestTests
{
    private static CaptureManifest Sample(string fuseVersion, int formatVersion = CaptureManifest.CurrentFormatVersion) =>
        new(formatVersion, fuseVersion, "abc123def456", "2026-07-09T00:00:00.0000000Z",
            [new CaptureProjectEntry("App", "App", ErrorCount: 0, TypeCount: 3, SymbolCount: 5, NodeCount: 4, EdgeCount: 2)]);

    [Fact]
    public void Manifest_round_trips_through_json_with_fields_intact()
    {
        var manifest = Sample(FuseBuildInfo.Current);
        var back = CaptureManifestJson.Deserialize(CaptureManifestJson.Serialize(manifest));

        Assert.NotNull(back);
        Assert.Equal(CaptureManifest.CurrentFormatVersion, back!.BundleFormatVersion);
        Assert.Equal(FuseBuildInfo.Current, back.FuseVersion);
        Assert.Equal("abc123def456", back.Commit);
        Assert.Single(back.Projects);
        Assert.Equal("App", back.Projects[0].Name);
        Assert.Equal(2, back.Projects[0].EdgeCount);
    }

    [Fact]
    public void A_bundle_from_the_running_version_is_compatible()
    {
        var manifest = Sample(FuseBuildInfo.Current);
        Assert.True(manifest.IsCompatibleWithRunningBuild);
        Assert.Null(manifest.IncompatibilityReason);
    }

    [Fact]
    public void A_bundle_from_an_incompatible_major_minor_is_refused_with_a_reason()
    {
        var manifest = Sample("0.1.0");
        Assert.False(manifest.IsCompatibleWithRunningBuild);
        Assert.NotNull(manifest.IncompatibilityReason);
        Assert.Contains("0.1.0", manifest.IncompatibilityReason!);
    }

    [Fact]
    public void A_bundle_with_an_unknown_format_version_is_refused_with_a_reason()
    {
        var manifest = Sample(FuseBuildInfo.Current, formatVersion: CaptureManifest.CurrentFormatVersion + 1);
        Assert.False(manifest.IsCompatibleWithRunningBuild);
        Assert.NotNull(manifest.IncompatibilityReason);
        Assert.Contains("format version", manifest.IncompatibilityReason!);
    }
}
