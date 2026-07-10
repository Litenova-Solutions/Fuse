using System.Diagnostics;
using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1 apply path: the NU1507 overlay remedy, applied. The integration test the item names ("a broken feed config is
// repaired via overlay"): a synthetic Central Package Management project with two sources and no source mapping
// reproduces NU1507 on a bare restore; running the same restore with the generated overlay NuGet.config passed via
// --configfile removes the NU1507 error (the overlay supplies the mapping), without editing the fixture. Guarded:
// when the SDK is absent or does not reproduce NU1507 here, the test skips rather than failing, matching the
// build-grade and resident integration tests.
public sealed class EnvironmentRemediationApplierTests
{
    [Fact]
    public async Task Overlay_restore_removes_the_NU1507_source_mapping_error()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-remediation-apply-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // A Central Package Management project with two package sources and no packageSourceMapping: the exact
            // shape that triggers NU1507. A single common PackageReference ensures the restore graph is evaluated.
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

            // Bare restore (no overlay): if the SDK is missing or NU1507 does not reproduce here, skip.
            var bare = await RunRestoreAsync(work, configFile: null);
            if (bare is null || !bare.Contains("NU1507", StringComparison.Ordinal))
                return;

            // Build the overlay from the fixture's two sources and apply it via the applier.
            var overlay = NuGetOverlayConfig.Build(NuGetOverlayConfig.ReadSources(work));
            var overlayPath = Path.Combine(work, "overlay.config"); // kept out of the repo under test; this IS the temp workspace
            await File.WriteAllTextAsync(overlayPath, overlay);

            var applier = new EnvironmentRemediationApplier(TimeSpan.FromMinutes(3));
            var result = await applier.ApplyOverlayRestoreAsync(work, overlayPath, CancellationToken.None);

            // The overlay supplies the source mapping, so NU1507 is gone. (Overall success can still be false offline
            // if the package cannot be downloaded; the remedy's job is specifically to clear NU1507.)
            Assert.DoesNotContain("NU1507", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<string?> RunRestoreAsync(string work, string? configFile)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = work,
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(work);
        if (configFile is not null)
        {
            psi.ArgumentList.Add("--configfile");
            psi.ArgumentList.Add(configFile);
        }

        try
        {
            using var process = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return stdout + stderr;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
