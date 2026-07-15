using System.Diagnostics;
using Fuse.Cli.Services;
using Fuse.Cli.Tests;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S1 issue-5 acceptance test (the edge-freshness gate scenario): an edited DI registration surfaces its new
// wiring edge through the store-backed read tools without a full re-index. It holds the OrderingApp fixture
// resident (mirrored to temp so the fixture is not edited), edits the composition root to register a brand-new
// service, applies the edit to the resident compilation, projects the changed project into a store, and confirms
// the new service now resolves to its implementation via SemanticResolver. Guarded: skips when OrderingApp does
// not build here.
public sealed class ResidentEdgeFreshnessTests
{
    [Fact]
    public async Task An_edited_DI_registration_becomes_queryable_after_resident_projection()
    {
        var fixture = FindFixture();
        if (fixture is null)
            return;

        var work = Path.Combine(Path.GetTempPath(), "fuse-edge-freshness-it", Guid.NewGuid().ToString("N"));
        CopyExcludingBuild(fixture, work);
        var binlog = Path.Combine(work, "build.binlog");
        var databasePath = Path.Combine(work, ".fuse", "fuse.db");
        try
        {
            var project = Path.Combine(work, "OrderingApp.csproj");
            if (!await TryBuildBinlogAsync(project, binlog))
                return; // OrderingApp did not build here; nothing to validate.

            var root = Path.GetFullPath(work);
            using var service = new ResidentWorkspaceService(root, ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None));

            // Edit the composition root: add a new service interface + implementation and register it.
            var programPath = Path.Combine(work, "Program.cs");
            var edited = (await File.ReadAllTextAsync(programPath))
                .Replace(
                    "        services.AddScoped<IShipping, FastShipping>();",
                    "        services.AddScoped<IShipping, FastShipping>();\n        services.AddScoped<INotifier, EmailNotifier>();")
                .Replace(
                    "public static class Program",
                    "public interface INotifier { void Notify(); }\npublic sealed class EmailNotifier : INotifier { public void Notify() { } }\n\npublic static class Program");
            await File.WriteAllTextAsync(programPath, edited);
            service.ApplyBatch([new WorkspaceFileChange(FileChangeKind.Changed, programPath)], CancellationToken.None);

            using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
            var indexer = provider.GetRequiredService<SemanticIndexer>();
            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);

            // Project the changed project's resident state into the store (no full re-index).
            var projected = await service.ProjectChangedAsync(indexer, store, [programPath], CancellationToken.None);
            Assert.True(projected >= 1, "expected the composition root's project to be re-projected");

            // The new registration is queryable: INotifier resolves to EmailNotifier via the projected DI edge.
            var resolution = await new SemanticResolver(store).ResolveServiceAsync("INotifier", CancellationToken.None);
            Assert.NotNull(resolution);
            Assert.NotEmpty(resolution!.Matches);
            Assert.Contains(resolution.Matches, m =>
                m.DisplayName.Contains("EmailNotifier", StringComparison.Ordinal)
                || (m.FilePath?.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) ?? false));

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static string? FindFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        var fixture = Path.Combine(dir.FullName, "tests", "fixtures", "OrderingApp");
        return Directory.Exists(fixture) ? fixture : null;
    }

    private static void CopyExcludingBuild(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split('/', '\\').Any(s => s is "bin" or "obj" or ".vs" or ".git" or ".fuse"))
                continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<bool> TryBuildBinlogAsync(string projectPath, string binlogPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
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
