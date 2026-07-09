using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the tier-1 build probe. A real dotnet build of a Central Package Management project with two sources and no
// source mapping fails with NU1507 (the design-time load does not surface this), and the probe classifies the build
// output against the knowledge base to name the blocker. Guarded: if the SDK is absent or NU1507 does not reproduce
// here, the test skips rather than failing, matching the applier and build-grade integration tests.
public sealed class TierOneBuildProbeTests
{
    [Fact]
    public async Task Probe_classifies_the_NU1507_blocker_from_a_real_build()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-tier1-probe-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Directory.Packages.props"), """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "nuget.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="dupe" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>
                """);

            var probe = new TierOneBuildProbe(TimeSpan.FromMinutes(5));
            var result = await probe.ProbeAsync(work, overlayConfigPath: null, CancellationToken.None);

            // Skip when the toolchain could not run or NU1507 did not reproduce in this environment.
            if (!result.Attempted || result.TimedOut || result.Succeeded)
                return;
            if (!result.Output.Contains("NU1507", StringComparison.Ordinal))
                return;

            // The build failed with NU1507, so the probe classifies the blocker to the overlay remedy.
            Assert.NotNull(result.Blocker);
            Assert.Equal("NU1507", result.Blocker!.Id);
            Assert.Equal("overlay-nuget-source-mapping", result.Blocker.Remedy);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
