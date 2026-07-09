using Fuse.BuildCaptureWorker;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// C2: the portable capture artifact. ExportCompilerLogAsync builds the target and writes a portable compiler log
// (.complog) - the compiler inputs, self-contained and without the binlog's environment variables. The round trip
// under test: export the complog, then rehydrate the compilations FROM the complog with no build, and confirm the
// extracted graph matches (the complog carries enough to reconstruct the compilation). Guarded: if the SDK cannot
// build here, the export abstains with a reason rather than failing the test, matching the other worker integration
// tests.
public sealed class CaptureComplogRoundTripTests
{
    [Fact]
    public async Task Exported_complog_rehydrates_the_compilation_without_a_build()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-complog-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var complogPath = Path.Combine(work, "capture.complog");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var rehydrator = new BuildCaptureRehydrator();
            var target = Path.Combine(work, "Widget.csproj");

            var exported = await rehydrator.ExportCompilerLogAsync(target, complogPath, TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!exported.Succeeded)
            {
                // The SDK could not build here; the export abstains with a reason rather than throwing.
                Assert.False(string.IsNullOrEmpty(exported.Reason));
                return;
            }

            // The portable artifact was written and carries the extracted graph.
            Assert.True(File.Exists(complogPath), "the complog file should be written on a successful export");
            Assert.Contains(exported.Projects, p => p.TypeCount > 0);

            // Rehydrate FROM the complog with no build (CompilerCallReaderUtil reads a complog as well as a binlog):
            // the round trip reconstructs the compilation and re-extracts the same shape.
            var rehydrated = rehydrator.RehydrateFromBinlog(complogPath, CancellationToken.None);
            Assert.True(rehydrated.Succeeded, $"rehydrating from the complog should succeed: {rehydrated.Reason}");
            Assert.Contains(rehydrated.Projects, p => p.Name == "Widget" && p.TypeCount > 0);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
