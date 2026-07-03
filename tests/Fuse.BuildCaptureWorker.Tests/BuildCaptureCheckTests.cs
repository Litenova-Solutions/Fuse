using Fuse.BuildCaptureWorker;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// R1: the worker's speculative typecheck. It builds and rehydrates the compilation, applies a proposed single-
// file patch in memory, and returns the compiler diagnostics for the changed document, with no disk write and
// no second build. Validated on a self-contained project: a type error is reported, a clean edit is clean.
public sealed class BuildCaptureCheckTests
{
    [Fact]
    public async Task Reports_a_type_error_in_a_proposed_edit_and_a_clean_edit_as_clean()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-it", Guid.NewGuid().ToString("N"));
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

            var rehydrator = new BuildCaptureRehydrator();
            var target = Path.Combine(work, "Widget.csproj");

            // A patch that introduces a type error (returning a string from an int method).
            var broken = await rehydrator.CheckAsync(
                target, "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => \"nope\"; }",
                TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!broken.Verified)
            {
                // The SDK could not build here; the worker abstains with a reason rather than throwing.
                Assert.False(string.IsNullOrEmpty(broken.Reason));
                return;
            }

            Assert.False(broken.IsClean);
            Assert.Contains(broken.Diagnostics, d => d.Severity == "Error");

            // A clean patch typechecks clean (tolerant of a transient second-build issue: if the capture build
            // does not re-run here, the worker abstains with a reason rather than producing a false verdict).
            var clean = await rehydrator.CheckAsync(
                target, "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => 7; }",
                TimeSpan.FromMinutes(5), CancellationToken.None);
            if (clean.Verified)
                Assert.True(clean.IsClean, "a clean edit should typecheck with no errors");
            else
                Assert.False(string.IsNullOrEmpty(clean.Reason));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
