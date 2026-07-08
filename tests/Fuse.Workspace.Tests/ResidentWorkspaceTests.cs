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

            // S4: the analyzer-aware overlay runs the compiler path plus any configured analyzers against the same
            // held state and merges the result. The clean edit stays error-free through this path too (it never
            // throws, and analyzers only add warnings if the project configured any); the broken edit still reports
            // the compiler error, so the merge does not drop compiler diagnostics.
            var cleanWithAnalyzers = await resident.CheckOverlayAsync(
                "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => 7; }",
                includeAnalyzers: true, CancellationToken.None);
            Assert.NotNull(cleanWithAnalyzers);
            Assert.DoesNotContain(cleanWithAnalyzers!, d => d.Severity == "Error");

            var brokenWithAnalyzers = await resident.CheckOverlayAsync(
                "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => Missing; }",
                includeAnalyzers: true, CancellationToken.None);
            Assert.NotNull(brokenWithAnalyzers);
            Assert.Contains(brokenWithAnalyzers!, d => d.Severity == "Error" && d.Id.StartsWith("CS", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Applied_edit_persists_in_resident_state_for_later_checks()
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
                return;

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None);

            // Apply an edit that adds a method; the change is retained in the resident compilation.
            var applied = resident.ApplyEdit(
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 42; public int Twice() => Spin() * 2; }",
                CancellationToken.None);
            Assert.True(applied);

            // A later overlay check that calls the newly added method binds against the retained edit (it would be
            // CS1061 if the resident state had not kept the ApplyEdit that introduced Twice).
            var afterEdit = resident.CheckOverlay(
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 42; public int Twice() => Spin() * 2; public int Quad() => Twice() * 2; }",
                CancellationToken.None);
            Assert.NotNull(afterEdit);
            Assert.DoesNotContain(afterEdit!, d => d.Severity == "Error");

            // Applying to a file not in any compilation reports no match.
            Assert.False(resident.ApplyEdit("Nowhere/Absent.cs", "class Z { }", CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Removed_document_leaves_resident_state_and_its_types_stop_binding()
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
            await File.WriteAllTextAsync(Path.Combine(work, "Helper.cs"),
                "namespace Sample; public static class Helper { public static int Base() => 1; }");

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return;

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None);
            var baseline = resident.GetDiagnostics(CancellationToken.None);
            Assert.DoesNotContain(baseline, d => d.Severity == "Error");

            // Remove Helper.cs from the resident state; a later check that references Helper no longer binds.
            Assert.True(resident.RemoveDocument("Helper.cs", CancellationToken.None));
            var afterRemoval = resident.CheckOverlay(
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Helper.Base(); }",
                CancellationToken.None);
            Assert.NotNull(afterRemoval);
            Assert.Contains(afterRemoval!, d => d.Severity == "Error");

            Assert.False(resident.RemoveDocument("Nowhere/Absent.cs", CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Added_document_binds_in_the_resident_state()
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

            // Add a brand-new file to the project's resident compilation.
            var added = resident.AddDocument(
                Path.Combine(work, "Helper.cs"),
                "namespace Sample; public static class Helper { public static int Base() => 1; }",
                CancellationToken.None);
            Assert.True(added);

            // A later check that references the new type binds (it would be CS0103/CS0246 if AddDocument had not
            // added Helper to the resident compilation).
            var afterAdd = resident.CheckOverlay(
                "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => Helper.Base(); }",
                CancellationToken.None);
            Assert.NotNull(afterAdd);
            Assert.DoesNotContain(afterAdd!, d => d.Severity == "Error");

            // A file under no held project is not attributed.
            Assert.False(resident.AddDocument(Path.Combine(Path.GetTempPath(), "Elsewhere.cs"), "class Z { }", CancellationToken.None));
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
