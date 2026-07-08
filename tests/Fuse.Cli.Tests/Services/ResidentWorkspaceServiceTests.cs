using System.Diagnostics;
using Fuse.Cli.Services;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S1: the concrete resident-workspace provider. It answers the availability description and resident-grade
// overlay checks for its root, and applies watcher batches to keep the held compilation current, advancing a
// revision. Backed by a real resident workspace built from a temp project binlog (guarded to skip when the SDK
// cannot produce one).
public sealed class ResidentWorkspaceServiceTests
{
    [Fact]
    public async Task Service_describes_its_root_checks_overlays_and_advances_on_batches()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-svc-it", Guid.NewGuid().ToString("N"));
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
            await File.WriteAllTextAsync(widget, "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return;

            var root = Path.GetFullPath(work);
            using var service = new ResidentWorkspaceService(root, ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None));

            // Describes its own root, not another.
            var status = service.DescribeResident(root);
            Assert.NotNull(status);
            Assert.Equal("revision 0", status!.AsOf);
            Assert.Null(service.DescribeResident(Path.Combine(Path.GetTempPath(), "other")));

            // Resident-grade overlay check answers for its root.
            var clean = service.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }", CancellationToken.None);
            Assert.NotNull(clean);
            Assert.DoesNotContain(clean!, d => d.Severity == "Error");
            Assert.Null(service.TryCheckOverlay(Path.Combine(Path.GetTempPath(), "other"), "Widget.cs", "x", CancellationToken.None));

            // Applying a batch that adds a Helper advances the revision, and a later check binds against it.
            var helper = Path.Combine(work, "Helper.cs");
            await File.WriteAllTextAsync(helper, "namespace Sample; public static class Helper { public static int Base() => 1; }");
            var result = service.ApplyBatch([new WorkspaceFileChange(FileChangeKind.Created, helper)], CancellationToken.None);
            Assert.Equal(1, result.Added);
            Assert.Equal("revision 1", service.DescribeResident(root)!.AsOf);

            var afterAdd = service.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Helper.Base(); }", CancellationToken.None);
            Assert.NotNull(afterAdd);
            Assert.DoesNotContain(afterAdd!, d => d.Severity == "Error");
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
