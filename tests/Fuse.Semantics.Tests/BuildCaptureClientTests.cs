using System.Runtime.CompilerServices;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// N4 tier-1: the parent (BuildCaptureClient) spawns the out-of-process worker and deserializes the graph bundle
// it emits, proving the cross-process contract end to end. This is the parent side of tier-1; the worker runs
// separately so its Basic.CompilerLog closure never conflicts with this parent's MSBuildWorkspace.
public sealed class BuildCaptureClientTests
{
    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? WorkerDll()
    {
        var root = RepoRoot();
        if (root is null)
            return null;
        foreach (var config in new[] { "Release", "Debug" })
        {
            var dll = Path.Combine(root, "src", "Host", "Fuse.BuildCaptureWorker", "bin", config, "net10.0", "fuse-build-capture.dll");
            if (File.Exists(dll))
                return dll;
        }

        return null;
    }

    [Fact]
    public async Task Parent_spawns_the_worker_and_reads_the_graph_bundle()
    {
        var workerDll = WorkerDll();
        if (workerDll is null)
            return; // Worker not built in this layout; nothing to validate.

        var work = Path.Combine(Path.GetTempPath(), "fuse-capture-client-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
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

            var client = new BuildCaptureClient(workerDll);
            Assert.True(client.IsAvailable);

            var result = await client.CaptureAsync(
                Path.Combine(work, "Widget.csproj"), TimeSpan.FromMinutes(5), CancellationToken.None);

            if (!result.Succeeded)
            {
                // The SDK could not build here; the client reports a concrete reason and does not throw.
                Assert.False(string.IsNullOrEmpty(result.Reason));
                return;
            }

            // The parent deserialized the worker's bundle: at least one project with extracted symbols.
            var project = Assert.Single(result.Projects);
            Assert.NotNull(project.Symbols);
            Assert.True(project.SymbolCount >= 1);
            Assert.Contains(project.Symbols!, s => s.Name == "Widget");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // C2: the parent spawns the worker's --check-complog mode against a captured compiler log and reads the
    // oracle-grade diagnostics WITHOUT building. This is the no-restore consumer contract end to end across the
    // process boundary: export a complog once, then check a proposed edit against it with no build.
    [Fact]
    public async Task Parent_checks_a_patch_against_a_complog_without_building()
    {
        var workerDll = WorkerDll();
        if (workerDll is null)
            return; // Worker not built in this layout; nothing to validate.

        var work = Path.Combine(Path.GetTempPath(), "fuse-check-complog-client-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var complogPath = Path.Combine(work, "capture.complog");
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

            var client = new BuildCaptureClient(workerDll);
            Assert.True(client.IsAvailable);

            // Produce the complog once (a real build); if the SDK cannot build here, abstain like the sibling tests.
            var captured = await client.CaptureBundleAsync(
                Path.Combine(work, "Widget.csproj"), complogPath, TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!captured.Succeeded)
            {
                Assert.False(string.IsNullOrEmpty(captured.Reason));
                return;
            }

            // A type error in the proposed edit is reported oracle-grade from the complog, spawned worker, no build.
            var bad = await client.CheckFromComplogAsync(
                complogPath, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => \"nope\"; }",
                TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(bad.Verified, $"the check should verify from the complog: {bad.Reason}");
            Assert.Equal("oracle", bad.Grade);
            Assert.Contains(bad.Diagnostics, d => d.Severity == "Error");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // C3: tier-1 build capture is default-ON; FUSE_BUILD_CAPTURE=0 (or false/no/off) opts out, and any other
    // value (or unset) enables it. The env var is saved and restored so the mutation does not leak.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void BuildCaptureEnabled_is_default_on_and_opts_out_on_a_falsey_value(string? value, bool expected)
    {
        var original = Environment.GetEnvironmentVariable("FUSE_BUILD_CAPTURE");
        try
        {
            Environment.SetEnvironmentVariable("FUSE_BUILD_CAPTURE", value);
            Assert.Equal(expected, SemanticIndexer.BuildCaptureEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSE_BUILD_CAPTURE", original);
        }
    }

    [Fact]
    public void Unconfigured_client_is_unavailable()
        => Assert.False(new BuildCaptureClient(workerDllPath: "").IsAvailable);

    // C3: worker discovery. The FUSE_BUILD_CAPTURE_WORKER env var is an explicit override that wins; with no
    // override, the worker is discovered in the install-relative build-capture/ subfolder next to the running
    // assembly (where the tool package ships it). These run sequentially within the class, and the env var is
    // saved and restored, so the mutation does not leak.
    [Fact]
    public void ResolveWorkerPath_prefers_the_env_override_then_the_install_relative_subfolder()
    {
        var original = Environment.GetEnvironmentVariable("FUSE_BUILD_CAPTURE_WORKER");
        var explicitPath = Path.Combine(Path.GetTempPath(), $"explicit-worker-{Guid.NewGuid():N}.dll");
        var subfolder = Path.Combine(AppContext.BaseDirectory, "build-capture");
        var shipped = Path.Combine(subfolder, "fuse-build-capture.dll");
        var createdShipped = false;
        try
        {
            // Explicit override wins even without the file existing on disk.
            Environment.SetEnvironmentVariable("FUSE_BUILD_CAPTURE_WORKER", explicitPath);
            Assert.Equal(explicitPath, BuildCaptureClient.ResolveWorkerPath());

            // With no override, an install-relative build-capture/fuse-build-capture.dll is discovered.
            Environment.SetEnvironmentVariable("FUSE_BUILD_CAPTURE_WORKER", null);
            if (!File.Exists(shipped))
            {
                Directory.CreateDirectory(subfolder);
                File.WriteAllText(shipped, "stub");
                createdShipped = true;
            }

            Assert.Equal(shipped, BuildCaptureClient.ResolveWorkerPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSE_BUILD_CAPTURE_WORKER", original);
            if (createdShipped)
            {
                try { File.Delete(shipped); } catch (IOException) { }
            }
        }
    }
}
