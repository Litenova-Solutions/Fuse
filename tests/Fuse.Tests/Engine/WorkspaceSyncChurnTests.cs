using System.Collections.Concurrent;
using Fuse.Tests.Fixtures;
using Fuse.Workspace.Generations;
namespace Fuse.Tests.Engine;

/// <summary>
/// How the workspace counts churn when the touched set grows: changed files never count, so more changed files than the bound do not re-evaluate on repeated syncs, and reverted files count once and read HEAD content after the re-evaluation.
/// </summary>
public class WorkspaceSyncChurnTests
{
    [Fact]
    public async Task More_changed_files_than_the_bound_do_not_reevaluate_on_repeated_syncs()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var lib = workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;
        await workspace.EnsureLoadedAsync([lib], cancellationToken);

        var relatives = Enumerable.Range(0, 8).Select(i => "Lib/Churn" + i + ".cs").ToList();
        foreach (var relative in relatives)
        {
            var index = relative["Lib/Churn".Length..^".cs".Length];
            repo.Write(relative, "namespace Lib;\n\npublic static class Churn" + index + "\n{\n    public static int Value() => " + index + ";\n}\n");
        }

        var paths = relatives.Select(repo.PathOf).ToList();
        var overlay = workspace.PrimaryOverlay;
        var sync = new WorkspaceSync(overlay.Tracker, overlay.Projects, overlay.Views, log.Enqueue, maxChurn: 3);
        await sync.SyncAsync(paths, cancellationToken);

        Assert.Equal(8, overlay.Tracker.Changed.Count);
        Assert.Equal(0, overlay.Views.ChurnCount);
        var generation = workspace.ConfigurationGeneration;

        await sync.SyncAsync([], cancellationToken);
        await sync.SyncAsync([], cancellationToken);

        Assert.Equal(generation, workspace.ConfigurationGeneration);
        Assert.DoesNotContain(log, l => l.Contains("touched set passed", StringComparison.Ordinal));
        Assert.Equal(0, overlay.Views.ChurnCount);
    }

    [Fact]
    public async Task Editing_then_reverting_more_files_than_the_bound_reevaluates_once_and_the_reverted_file_reads_head_content()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var lib = workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;
        await workspace.EnsureLoadedAsync([lib], cancellationToken);

        var originalText = repo.Read("Lib/Calc.cs");
        var originalBytes = File.ReadAllBytes(repo.Full("Lib/Calc.cs"));
        var created = Enumerable.Range(0, 7).Select(i => "Lib/Churn" + i + ".cs").ToList();
        foreach (var relative in created)
        {
            var index = relative["Lib/Churn".Length..^".cs".Length];
            repo.Write(relative, "namespace Lib;\n\npublic static class Churn" + index + "\n{\n    public static int Value() => " + index + ";\n}\n");
        }

        repo.Write("Lib/Calc.cs", originalText + "\n// churn\n");
        var edited = created.Select(repo.PathOf).Append(repo.PathOf("Lib/Calc.cs")).ToList();
        var overlay = workspace.PrimaryOverlay;
        var sync = new WorkspaceSync(overlay.Tracker, overlay.Projects, overlay.Views, log.Enqueue, maxChurn: 3);
        await sync.SyncAsync(edited, cancellationToken);
        var afterEdit = workspace.ConfigurationGeneration;

        await sync.SyncAsync([], cancellationToken);
        Assert.Equal(afterEdit, workspace.ConfigurationGeneration);

        foreach (var relative in created)
            File.Delete(repo.Full(relative));
        File.WriteAllBytes(repo.Full("Lib/Calc.cs"), originalBytes);
        var reverted = created.Select(repo.PathOf).Append(repo.PathOf("Lib/Calc.cs")).ToList();
        await sync.SyncAsync(reverted, cancellationToken);

        Assert.Equal(afterEdit + 1, workspace.ConfigurationGeneration);
        Assert.Single(log, l => l.StartsWith("reloading: Reevaluate trigger=touched set passed", StringComparison.Ordinal));

        await sync.SyncAsync([], cancellationToken);
        Assert.Equal(afterEdit + 1, workspace.ConfigurationGeneration);
        Assert.Single(log, l => l.StartsWith("reloading: Reevaluate trigger=touched set passed", StringComparison.Ordinal));
        // A watcher event for a reverted file can arrive after the re-evaluation and touch it once more; that is
        // churn under the bound, which re-evaluates nothing, and no file differs from HEAD.
        Assert.True(overlay.Views.ChurnCount <= 3, $"churn {overlay.Views.ChurnCount} passed the bound");
        Assert.Empty(overlay.Tracker.Changed);

        var ids = workspace.Current.GetDocumentIdsWithFilePath(repo.Full("Lib/Calc.cs"));
        Assert.NotEmpty(ids);
        var text = (await workspace.Current.GetDocument(ids[0])!.GetTextAsync(cancellationToken)).ToString();
        Assert.DoesNotContain("// churn", text, StringComparison.Ordinal);
        Assert.Contains("a * b;", text, StringComparison.Ordinal);
    }
}
