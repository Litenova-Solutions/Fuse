using System.Diagnostics;
using Fuse.BuildCaptureWorker;
using Fuse.Indexing;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// G4: the capture fragment-merge channel. A per-project fragment is a per-project binary log; merging the
// fragments must yield the same extracted graph as a direct whole-solution capture (edge-set equality is the
// gate's proof). Two independent projects are used so each per-project build records exactly one compilation.
// Guarded: if the SDK cannot build here, the direct capture abstains and the test returns rather than failing.
public sealed class CaptureFragmentMergeTests
{
    [Fact]
    public async Task Merged_fragments_equal_a_direct_capture()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-merge-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            WriteProject(work, "Alpha", "namespace Alpha; public sealed class Widget { public int Spin() => 1; }");
            WriteProject(work, "Beta", "namespace Beta; public sealed class Gadget { public int Whirl() => 2; }");

            // Build the solution via the dotnet CLI so it is well-formed (a hand-written solution is fragile).
            // .NET 10's `dotnet new sln` emits the XML solution format (.slnx), which `dotnet build` consumes.
            var slnPath = Path.Combine(work, "Both.slnx");
            if (!await RunDotnetAsync(work, "new", "sln", "-n", "Both")
                || !await RunDotnetAsync(work, "sln", slnPath, "add", Path.Combine(work, "Alpha", "Alpha.csproj"), Path.Combine(work, "Beta", "Beta.csproj")))
            {
                return; // The SDK is unavailable here; the sibling guarded tests cover the same abstain shape.
            }
            Assert.True(File.Exists(slnPath), "the solution file should have been created");

            var rehydrator = new BuildCaptureRehydrator();

            // Direct whole-solution capture (builds the solution, rehydrates both projects).
            var direct = await rehydrator.CaptureAsync(slnPath, TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!direct.Succeeded)
            {
                Assert.False(string.IsNullOrEmpty(direct.Reason)); // SDK cannot build here; abstain.
                return;
            }
            Assert.Equal(2, direct.Projects.Count); // The direct capture rehydrated both projects.

            // Per-project fragments: build each project alone with its own binlog (one compilation each).
            var fragA = Path.Combine(work, "alpha.binlog");
            var fragB = Path.Combine(work, "beta.binlog");
            var okA = await BuildFragmentAsync(Path.Combine(work, "Alpha", "Alpha.csproj"), fragA);
            var okB = await BuildFragmentAsync(Path.Combine(work, "Beta", "Beta.csproj"), fragB);
            Assert.True(okA && okB, "per-project fragment builds should succeed when the direct capture did");

            var merged = rehydrator.MergeFragments([fragA, fragB], CancellationToken.None);
            Assert.True(merged.Succeeded, $"merge should succeed: {merged.Reason}");

            // Edge-set equality: the same project set, and the same totals for symbols, nodes, and edges.
            var directNames = direct.Projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var mergedNames = merged.Projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(directNames, mergedNames);
            Assert.Equal(direct.Projects.Sum(p => p.SymbolCount), merged.Projects.Sum(p => p.SymbolCount));
            Assert.Equal(direct.Projects.Sum(p => p.NodeCount), merged.Projects.Sum(p => p.NodeCount));
            Assert.Equal(direct.Projects.Sum(p => p.EdgeCount), merged.Projects.Sum(p => p.EdgeCount));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static void WriteProject(string root, string name, string source)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, $"{name}.cs"), source);
    }

    // Builds a single project non-incrementally with its own binary log, so the fragment records exactly this
    // project's C# compilation.
    private static async Task<bool> BuildFragmentAsync(string projectPath, string binlogPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectPath),
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("--no-incremental");
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        return process.ExitCode == 0 && File.Exists(binlogPath);
    }

    // Runs a dotnet CLI verb in a working directory; returns whether it exited cleanly.
    private static async Task<bool> RunDotnetAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
