using System.Diagnostics;
using Fuse.Cli.Services;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S1 step 3: the glue that applies a coalesced watcher batch to a resident workspace. It reads new content from
// disk for created/changed C# files and edits (or adds) the held document, and removes documents for deleted
// files, never writing the tree. The test builds a real resident workspace from a temp project binlog (guarded
// to skip when the SDK cannot produce one), mutates files on disk, and feeds the corresponding batch.
public sealed class ResidentWorkspaceUpdaterTests
{
    [Fact]
    public async Task Apply_edits_adds_and_removes_documents_from_disk_changes()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-updater-it", Guid.NewGuid().ToString("N"));
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
            var widget = Path.Combine(work, "Widget.cs");
            var spare = Path.Combine(work, "Spare.cs");
            await File.WriteAllTextAsync(widget, "namespace Sample; public sealed class Widget { public int Spin() => 42; }");
            await File.WriteAllTextAsync(spare, "namespace Sample; public sealed class Spare { }");

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return;

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None);
            var updater = new ResidentWorkspaceUpdater();

            // Change Widget on disk, create a new Helper, delete Spare, and touch a non-cs file.
            await File.WriteAllTextAsync(widget, "namespace Sample; public sealed class Widget { public int Spin() => Helper.Base(); }");
            var helper = Path.Combine(work, "Helper.cs");
            await File.WriteAllTextAsync(helper, "namespace Sample; public static class Helper { public static int Base() => 1; }");
            File.Delete(spare);

            var result = updater.Apply(resident, [
                new WorkspaceFileChange(FileChangeKind.Changed, widget),
                new WorkspaceFileChange(FileChangeKind.Created, helper),
                new WorkspaceFileChange(FileChangeKind.Deleted, spare),
                new WorkspaceFileChange(FileChangeKind.Changed, Path.Combine(work, "README.md")),
            ], CancellationToken.None);

            Assert.Equal(1, result.Applied);
            Assert.Equal(1, result.Added);
            Assert.Equal(1, result.Removed);
            Assert.Equal(1, result.Skipped); // the .md file

            // The resident state now reflects all three .cs changes: Widget's edit references Helper (added), and
            // it binds, which requires both the applied edit and the added Helper document.
            var check = resident.CheckOverlay(
                "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => Helper.Base(); }",
                CancellationToken.None);
            Assert.NotNull(check);
            Assert.DoesNotContain(check!, d => d.Severity == "Error");
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
