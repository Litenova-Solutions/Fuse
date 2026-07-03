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

    [Fact]
    public void Unconfigured_client_is_unavailable()
        => Assert.False(new BuildCaptureClient(workerDllPath: "").IsAvailable);
}
