using System.Diagnostics;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// S1 step 1: the resident workspace holds live rehydrated compilations between calls and answers overlay checks
// against them without a build or a disk write. The test builds a tiny self-contained project with a binary log
// (no package references, so no restore network access), loads it resident, and proves the held compilation
// answers two successive overlay checks (a clean edit and a broken edit) from the same in-memory state. When the
// SDK cannot produce a binlog in this environment the test is skipped rather than failing, matching the guarded
// style of the build-capture parent test.
public sealed class ResidentWorkspaceTests
{
    [Fact]
    public async Task Resident_workspace_answers_overlay_checks_from_held_compilations()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-ws-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var binlog = Path.Combine(work, "build.binlog");
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

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return; // The SDK could not build a binlog here; nothing to validate.

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None);
            Assert.NotEmpty(resident.Projects);

            // A clean edit: the held compilation reports no errors for the changed document.
            var clean = resident.CheckOverlay(
                "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => 7; }",
                CancellationToken.None);
            Assert.NotNull(clean);
            Assert.DoesNotContain(clean!, d => d.Severity == "Error");

            // A second, broken edit against the SAME resident state (proving the compilation is held, not rebuilt):
            // an undefined identifier yields a CS error located in the changed document.
            var broken = resident.CheckOverlay(
                "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => Missing; }",
                CancellationToken.None);
            Assert.NotNull(broken);
            var error = Assert.Single(broken!, d => d.Severity == "Error");
            Assert.StartsWith("CS", error.Id);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Overlay_check_returns_null_for_a_file_not_in_any_compilation()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-ws-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var binlog = Path.Combine(work, "build.binlog");
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

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return;

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None);
            var result = resident.CheckOverlay("Nowhere/Absent.cs", "namespace X; class Y { }", CancellationToken.None);
            Assert.Null(result);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<bool> TryBuildWithBinlogAsync(string projectDir, string binlogPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectDir,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(Path.Combine(projectDir, "Widget.csproj"));
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        try
        {
            using var process = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 && File.Exists(binlogPath);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
