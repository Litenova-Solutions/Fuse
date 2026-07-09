using Fuse.BuildCaptureWorker;
using Fuse.Indexing;
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

    // C2 gate: the bundle round trip. Export a complog, write a bundle from the captured graph, then read the bundle
    // back with no build and confirm the manifest and the graph survive the round trip unchanged - the same projects,
    // and per project the same symbol, node, and edge counts. This is the edge-set equality the C2 gate names: what a
    // producer captures is exactly what a consumer rehydrates, verified without re-running the build.
    [Fact]
    public async Task Bundle_round_trip_preserves_the_captured_graph()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-bundle-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var complogPath = Path.Combine(work, "capture.complog");
        var bundleDir = Path.Combine(work, "bundle");
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
                Assert.False(string.IsNullOrEmpty(exported.Reason));
                return;
            }

            // Write the bundle from the captured graph (moves the complog in, serializes graph.json, stamps manifest).
            var written = CaptureBundleIo.Write(bundleDir, complogPath, exported, commit: "deadbeef", capturedUtc: "2026-07-09T00:00:00Z");
            Assert.Equal(CaptureManifest.CurrentFormatVersion, written.BundleFormatVersion);

            // Read the bundle back with no build and confirm the manifest survived unchanged.
            var manifest = CaptureBundleIo.ReadManifest(bundleDir);
            Assert.NotNull(manifest);
            Assert.True(manifest!.IsCompatibleWithRunningBuild, manifest.IncompatibilityReason);
            Assert.Equal("deadbeef", manifest.Commit);
            Assert.Equal(exported.Projects.Count, manifest.Projects.Count);

            // Read the graph back and confirm edge-set equality: same projects, same per-project symbol/node/edge counts.
            var graph = CaptureBundleIo.ReadGraph(bundleDir);
            Assert.NotNull(graph);
            Assert.Equal(exported.Projects.Count, graph!.Projects.Count);
            foreach (var original in exported.Projects)
            {
                var readBack = Assert.Single(graph.Projects, p => p.Name == original.Name);
                Assert.Equal(original.SymbolCount, readBack.SymbolCount);
                Assert.Equal(original.NodeCount, readBack.NodeCount);
                Assert.Equal(original.EdgeCount, readBack.EdgeCount);
                Assert.Equal(original.TypeCount, readBack.TypeCount);

                // The records themselves round trip, not just the counts: the extracted symbol identities survive
                // serialization into and out of the bundle unchanged.
                var originalSymbols = (original.Symbols ?? []).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal);
                var readBackSymbols = (readBack.Symbols ?? []).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal);
                Assert.Equal(originalSymbols, readBackSymbols);
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // C2 gate: the oracle-grade check from a captured compiler log, WITHOUT building. Export a complog, then check a
    // proposed edit against it with no build: a type error in the edit is reported (the oracle answer a no-restore
    // machine gets from the bundle), and a clean edit reports no errors. This is the check answer the C2 gate names
    // as the payoff - correct oracle-grade diagnostics on a machine that cannot restore or build.
    [Fact]
    public async Task CheckFromLog_reports_a_known_bad_edit_and_a_clean_edit_without_building()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-complog-check-it", Guid.NewGuid().ToString("N"));
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
                Assert.False(string.IsNullOrEmpty(exported.Reason));
                return;
            }

            // A type error in the proposed edit is reported oracle-grade from the complog, with no build.
            var bad = rehydrator.CheckFromLog(complogPath, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => \"not an int\"; }", CancellationToken.None);
            Assert.True(bad.Verified, $"the check should verify from the complog: {bad.Reason}");
            Assert.Contains(bad.Diagnostics, d => d.Severity == "Error");

            // A clean edit reports no errors from the same complog.
            var good = rehydrator.CheckFromLog(complogPath, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; public int Twirl() => 8; }", CancellationToken.None);
            Assert.True(good.Verified, $"the clean check should verify from the complog: {good.Reason}");
            Assert.DoesNotContain(good.Diagnostics, d => d.Severity == "Error");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
