using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// Version compatibility gate that drives the index self-heal: compatible by major.minor, null treated as compatible.
public sealed class FuseBuildInfoTests
{
    [Fact]
    public void NullOrEmptyStoredVersionIsCompatible()
    {
        // A pre-stamp index carries no version; it must not be wiped blindly.
        Assert.True(FuseBuildInfo.IsCompatible(null));
        Assert.True(FuseBuildInfo.IsCompatible(""));
        Assert.True(FuseBuildInfo.IsCompatible("   "));
    }

    [Fact]
    public void SameMajorMinorIsCompatibleAcrossPatchAndMetadata()
    {
        Assert.True(FuseBuildInfo.IsCompatible(FuseBuildInfo.Current));

        var current = FuseBuildInfo.Current;
        var parts = current.Split('.');
        if (parts.Length >= 2)
        {
            // A different patch and a +sha build-metadata suffix keep the same major.minor, so both are compatible.
            Assert.True(FuseBuildInfo.IsCompatible($"{parts[0]}.{parts[1]}.999"));
            Assert.True(FuseBuildInfo.IsCompatible($"{parts[0]}.{parts[1]}.0+abcdef1"));
        }
    }

    [Fact]
    public void DifferentMajorMinorIsIncompatible()
    {
        // An unreachable major forces the mismatch regardless of the actual running version.
        Assert.False(FuseBuildInfo.IsCompatible("999999.0.0"));
    }
}
