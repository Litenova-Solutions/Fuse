using Fuse.Repo;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>A git-ignored file the watcher reports is not a changed non-C# file, however many arrive in one sync.</summary>
public class IgnoredNonSourceTests
{
    [Fact]
    public async Task An_ignored_non_csharp_file_is_not_named_by_a_check()
    {
        await using var engine = await InProcessEngine.StartAsync(WithIgnoredFolder());
        engine.Repo.Write("ignored/data.json", "{}\n");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var report = await engine.CheckAllAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(0, report.NonCSharpChanged);
        Assert.Empty(report.NonCSharpSample);
        Assert.Empty(engine.Workspace.Tracker.NonSources);
    }

    [Fact]
    public async Task An_ignored_non_csharp_file_gives_no_outside_line_in_a_plan()
    {
        await using var engine = await InProcessEngine.StartAsync(WithIgnoredFolder());
        engine.Repo.Write("ignored/consumer.json", "{}\n");
        await engine.SyncAsync("ignored/consumer.json");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Contains("no C# changes since HEAD", plan.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("outside every project", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_watcher_reported_ignored_non_source_is_filtered_and_a_kept_one_is_not()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "ignored/\n",
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
            ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
        });
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        repo.Write("ignored/data.json", "{}\n");
        repo.Write("notes/keep.json", "{}\n");
        watched.Record(repo.Full("ignored/data.json"), appearedOrVanished: true);
        watched.Record(repo.Full("notes/keep.json"), appearedOrVanished: true);

        await tracker.SyncAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(1, tracker.NonSourceChanged);
        Assert.Equal([repo.PathOf("notes/keep.json")], tracker.NonSources);
        Assert.Equal([repo.PathOf("notes/keep.json")], tracker.NonSourceSample);
    }

    [Fact]
    public async Task Many_ignored_non_sources_in_one_sync_are_all_filtered()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "ignored/\n",
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
            ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
        });
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        for (var i = 0; i < 20; i++)
        {
            repo.Write($"ignored/file{i}.json", "{}\n");
            watched.Record(repo.Full($"ignored/file{i}.json"), appearedOrVanished: true);
        }

        await tracker.SyncAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(0, tracker.NonSourceChanged);
        Assert.Empty(tracker.NonSources);
    }

    /// <summary>The standard repository with an ignored folder in its gitignore.</summary>
    private static FixtureRepo WithIgnoredFolder()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write(".gitignore", "bin/\nobj/\nignored/\n");
        repo.Commit("ignore a folder");
        return repo;
    }

    private static async Task<ChangeTracker> StartAsync(FixtureRepo repo, WatchedPaths watched)
    {
        var tracker = new ChangeTracker(repo.Root, watched);
        await tracker.InitializeAsync(TestContext.Current.CancellationToken);
        return tracker;
    }
}
