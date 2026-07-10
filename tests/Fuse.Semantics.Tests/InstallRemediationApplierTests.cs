using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the consent-gated SDK-band install remedy. The deterministic, offline part under test is reading the pinned
// band from global.json (the band the NETSDK1045 remedy installs). The actual install (dotnet-install script) is
// machine-changing and network-bound, exercised only behind --allow-install and validated separately.
public sealed class InstallRemediationApplierTests
{
    [Fact]
    public void TryReadPinnedSdkBand_reads_the_sdk_version_from_global_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-sdk-pin-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "global.json"), """
                { "sdk": { "version": "7.0.100", "rollForward": "disable" } }
                """);

            Assert.Equal("7.0.100", InstallRemediationApplier.TryReadPinnedSdkBand(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TryReadPinnedSdkBand_returns_null_when_no_global_json_pins_a_band()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-sdk-pin-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A global.json without an sdk.version (only other settings) pins no band.
            File.WriteAllText(Path.Combine(dir, "global.json"), """
                { "msbuild-sdks": { "Some.Sdk": "1.0.0" } }
                """);

            Assert.Null(InstallRemediationApplier.TryReadPinnedSdkBand(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
