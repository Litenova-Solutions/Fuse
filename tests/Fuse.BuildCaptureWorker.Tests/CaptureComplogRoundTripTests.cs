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

    // Regression: the worker must key extracted symbol file paths to the WORKSPACE ROOT it is given, not to each
    // project's directory. On a nested layout (project in a subdirectory) the two diverge, and a project-relative
    // path never resolves against the consumer's root-relative file rows, so every symbol is dropped. Building the
    // same nested project and rehydrating with an explicit workspaceRoot must produce a root-relative symbol path;
    // rehydrating with no root reproduces the legacy project-relative basis. Self-guards on the SDK like the tests
    // above.
    [Fact]
    public async Task Rehydrate_keys_symbol_paths_to_the_workspace_root_when_given()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-root-basis-it", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(work, "src", "App");
        Directory.CreateDirectory(projectDir);
        var complogPath = Path.Combine(work, "capture.complog");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(projectDir, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(projectDir, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var rehydrator = new BuildCaptureRehydrator();
            var target = Path.Combine(projectDir, "App.csproj");

            var exported = await rehydrator.ExportCompilerLogAsync(target, complogPath, TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!exported.Succeeded)
            {
                Assert.False(string.IsNullOrEmpty(exported.Reason)); // SDK could not build here; abstain.
                return;
            }

            // With the workspace root, the Widget symbol's path is relative to the root (src/App/Widget.cs), so it
            // matches the store's root-relative file rows and the symbol survives the file-id join.
            var rooted = rehydrator.RehydrateFromBinlog(complogPath, work, CancellationToken.None);
            var rootedWidget = rooted.Projects.SelectMany(p => p.Symbols ?? []).FirstOrDefault(s => s.Name == "Widget");
            Assert.NotNull(rootedWidget);
            Assert.Equal("src/App/Widget.cs", rootedWidget!.FilePath);

            // With no root the legacy project-directory basis returns just the file name - the bug that dropped symbols.
            var unrooted = rehydrator.RehydrateFromBinlog(complogPath, workspaceRoot: null, CancellationToken.None);
            var unrootedWidget = unrooted.Projects.SelectMany(p => p.Symbols ?? []).FirstOrDefault(s => s.Name == "Widget");
            Assert.NotNull(unrootedWidget);
            Assert.Equal("Widget.cs", unrootedWidget!.FilePath);
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

    // C3 regression: capture must succeed on an ALREADY-BUILT repository. Rehydration reads the Csc invocations
    // from the binary log, but an up-to-date incremental build emits none ("the build log recorded no C# compiler
    // invocations"). The build forces --no-incremental so a second capture of the same tree still carries the
    // compiler calls. Capturing twice proves it: the second run builds against a warm/up-to-date tree.
    [Fact]
    public async Task Capture_of_an_already_built_project_still_rehydrates()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-rebuilt-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var rehydrator = new BuildCaptureRehydrator();
            var target = Path.Combine(work, "Widget.csproj");

            var first = await rehydrator.CaptureAsync(target, TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!first.Succeeded)
            {
                Assert.False(string.IsNullOrEmpty(first.Reason)); // SDK cannot build here; abstain.
                return;
            }

            // Second capture against the now-built tree: with --no-incremental the compiler still runs, so the
            // binlog carries Csc calls and rehydration finds the project rather than failing with an empty log.
            var second = await rehydrator.CaptureAsync(target, TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.True(second.Succeeded, $"capture of an already-built project should still rehydrate: {second.Reason}");
            Assert.Contains(second.Projects, p => p.Name == "Widget" && p.TypeCount > 0);
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

    // Regression: a tier-1 capture serializes each compiler invocation separately. The cross-project tests edges
    // must be projected after those graphs are merged, just as SemanticIndexer.RunAnalyzers does for the ordinary
    // workspace path; otherwise fuse_test sees no covering tests in the default build-capture mode.
    [Fact]
    public async Task Capture_projects_include_cross_project_covering_test_edges()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-capture-test-edges-it", Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(work, "App");
        var testDir = Path.Combine(work, "App.Tests");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(testDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(appDir, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Calculator.cs"), """
                namespace SmokeApp;
                public sealed class Calculator { public int Add(int left, int right) => left + right; }
                """);
            await File.WriteAllTextAsync(Path.Combine(testDir, "App.Tests.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../App/App.csproj" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(testDir, "TestFramework.cs"), """
                namespace Xunit;
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class FactAttribute : System.Attribute { }
                """);
            await File.WriteAllTextAsync(Path.Combine(testDir, "CalculatorTests.cs"), """
                using SmokeApp;
                using Xunit;
                namespace SmokeApp.Tests;
                public sealed class CalculatorTests
                {
                    [Fact] public void Add_returns_sum() => _ = new Calculator().Add(2, 3);
                }
                """);

            var capture = await new BuildCaptureRehydrator().CaptureAsync(
                Path.Combine(testDir, "App.Tests.csproj"),
                TimeSpan.FromMinutes(5),
                CancellationToken.None,
                workspaceRoot: work);
            Assert.True(capture.Succeeded, $"capture should succeed: {capture.Reason}");
            Assert.Contains(
                capture.Projects.SelectMany(project => project.Edges ?? []),
                edge => edge is
                {
                    FromNodeId: "type:SmokeApp.Tests.CalculatorTests",
                    ToNodeId: "type:SmokeApp.Calculator",
                    EdgeType: "tests",
                });
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
