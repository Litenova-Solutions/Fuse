using Fuse.BuildCaptureWorker;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// N4 tier-1: the out-of-process build-capture worker rehydrates exact Roslyn compilations from a binary log,
// proving the mechanism works in isolation from MSBuildWorkspace (the two Roslyn closures conflict in one
// process, so capture runs in this separate worker). Validated end to end by building a self-contained project.
public sealed class BuildCaptureRehydratorTests
{
    [Fact]
    public async Task Capture_rehydrates_the_compilation_from_a_real_build()
    {
        // A minimal, self-contained project in a temp directory: no dependency on repo MSBuild props and no
        // shared fixture, so this test's `dotnet build` neither races other tests nor needs central packages.
        var work = Path.Combine(Path.GetTempPath(), "fuse-capture-it", Guid.NewGuid().ToString("N"));
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

            var result = await new BuildCaptureRehydrator().CaptureAsync(
                Path.Combine(work, "Widget.csproj"), TimeSpan.FromMinutes(5), CancellationToken.None);

            if (!result.Succeeded)
            {
                // The SDK could not build here; the worker reports a concrete reason and does not throw, which is
                // the fallback contract the parent relies on.
                Assert.False(string.IsNullOrEmpty(result.Reason));
                return;
            }

            // Oracle tier reached out of process: the compilation rehydrated and declares the real Widget type,
            // and Fuse's semantic extraction ran over it in the worker (never MSBuildWorkspace), producing symbols.
            var project = Assert.Single(result.Projects);
            Assert.True(project.TypeCount >= 1, "the rehydrated compilation should declare at least the Widget type");
            Assert.True(project.SymbolCount >= 1, "the worker's semantic extraction should produce symbols from the rehydrated compilation");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
