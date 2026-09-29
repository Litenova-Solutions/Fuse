using Fuse.Failures;
using Fuse.Paths;
using Fuse.Repo;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Each sync tells the workspace how to follow the changes it found: evaluate the projects again when their
///     configuration may have changed, reload when more files changed than patching one at a time is worth, and patch
///     otherwise. These cases record the watcher's events themselves, so the answer does not depend on when the file
///     system delivers them.
/// </summary>
public class ChangeTrackerTests
{
    [Fact]
    public async Task An_edited_source_is_patched()
    {
        using var repo = Repo();
        using var tracker = await StartAsync(repo, new WatchedPaths(repo.Root));
        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");

        var patch = Assert.IsType<SyncResult.Patch>(await tracker.SyncAsync([repo.PathOf("Lib/Calc.cs")], TestContext.Current.CancellationToken));

        Assert.Equal([repo.PathOf("Lib/Calc.cs")], patch.Paths);
        Assert.Empty(patch.VanishedDirectories);
        Assert.Equal([repo.PathOf("Lib/Calc.cs")], tracker.Changed);
    }

    [Fact]
    public async Task A_deleted_directory_is_patched_with_the_changed_sources_under_it()
    {
        using var repo = Repo();
        // Untracked, so the seed from git status holds it before the directory goes.
        repo.Write("Lib/New/Extra.cs", "public class Extra {}\n");
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        Assert.Contains(repo.PathOf("Lib/New/Extra.cs"), tracker.Changed);

        Directory.Delete(repo.Full("Lib/New"), recursive: true);
        watched.Record(repo.Full("Lib/New"), appearedOrVanished: true);
        var patch = Assert.IsType<SyncResult.Patch>(await tracker.SyncAsync([], TestContext.Current.CancellationToken));

        Assert.Equal([repo.PathOf("Lib/New")], patch.VanishedDirectories);
        Assert.Contains(repo.PathOf("Lib/New/Extra.cs"), patch.Paths);
        // The file is neither at HEAD nor on disk now, so it no longer differs from HEAD.
        Assert.Empty(tracker.Changed);
    }

    [Fact]
    public async Task A_project_file_change_reevaluates()
    {
        using var repo = Repo();
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");
        watched.Record(repo.Full("Lib/Lib.csproj"), appearedOrVanished: false);

        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(await tracker.SyncAsync([repo.PathOf("Lib/Calc.cs")], TestContext.Current.CancellationToken));

        Assert.Equal(repo.Full("Lib/Lib.csproj"), reevaluate.Trigger);
        Assert.Contains(repo.PathOf("Lib/Calc.cs"), reevaluate.Paths);
        Assert.Equal([repo.PathOf("Lib/Calc.cs")], tracker.Changed);
    }

    [Fact]
    public async Task A_moved_head_reevaluates_and_names_the_commit()
    {
        using var repo = Repo();
        using var tracker = await StartAsync(repo, new WatchedPaths(repo.Root));
        var before = tracker.Head;
        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");
        repo.Commit("move HEAD");
        var head = FixtureRepo.Run(repo.Root.Path, "git", "rev-parse", "HEAD").Trim();

        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(await tracker.SyncAsync([], TestContext.Current.CancellationToken));

        Assert.NotEqual(before, head);
        Assert.Equal(head, tracker.Head);
        Assert.Equal($"HEAD moved to {head[..12]}", reevaluate.Trigger);
        // The commit holds the edit, so after the seed from git status nothing differs from HEAD.
        Assert.Empty(tracker.Changed);
    }

    [Fact]
    public async Task A_watcher_error_reevaluates_and_seeds_again_from_git()
    {
        using var repo = Repo();
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");
        watched.RecordError("Too many changes at once in directory");

        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(await tracker.SyncAsync([], TestContext.Current.CancellationToken));

        Assert.Equal("watcher error: Too many changes at once in directory", reevaluate.Trigger);
        // git status finds the edit whether or not its event arrived.
        Assert.Contains(repo.PathOf("Lib/Calc.cs"), reevaluate.Paths);
        Assert.Equal([repo.PathOf("Lib/Calc.cs")], tracker.Changed);
    }

    [Fact]
    public async Task More_than_300_changed_sources_reload()
    {
        using var repo = Repo();
        using var tracker = await StartAsync(repo, new WatchedPaths(repo.Root));
        var reported = Generated(repo, 301);

        var reload = Assert.IsType<SyncResult.Reload>(await tracker.SyncAsync(reported, TestContext.Current.CancellationToken));

        Assert.Equal("301 changed files", reload.Trigger);
        Assert.All(reported, path => Assert.Contains(path, reload.Paths));
    }

    [Fact]
    public async Task Exactly_300_changed_sources_are_still_patched()
    {
        using var repo = Repo();
        using var tracker = await StartAsync(repo, new WatchedPaths(repo.Root));
        var reported = Generated(repo, 300);

        var patch = Assert.IsType<SyncResult.Patch>(await tracker.SyncAsync(reported, TestContext.Current.CancellationToken));

        Assert.Equal(300, patch.Paths.Count);
        // None of them exists on either side, so none differs from HEAD.
        Assert.Empty(tracker.Changed);
    }

    [Fact]
    public async Task A_project_file_among_more_than_300_changes_reevaluates()
    {
        using var repo = Repo();
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        watched.Record(repo.Full("Lib/Lib.csproj"), appearedOrVanished: false);

        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(await tracker.SyncAsync(Generated(repo, 301), TestContext.Current.CancellationToken));

        Assert.Equal(repo.Full("Lib/Lib.csproj"), reevaluate.Trigger);
    }

    [Fact]
    public async Task A_reseed_that_fails_after_head_moved_keeps_head_and_the_changes_and_the_next_sync_retries()
    {
        using var repo = Repo();
        using var tracker = await StartAsync(repo, new WatchedPaths(repo.Root));
        repo.Write("Lib/Extra.cs", "public class Extra {}\n");
        await tracker.SyncAsync([repo.PathOf("Lib/Extra.cs")], TestContext.Current.CancellationToken);
        var before = tracker.Head;
        // An empty commit moves HEAD and leaves Extra.cs untracked, so it still differs from HEAD afterwards.
        FixtureRepo.Run(repo.Root.Path, "git", "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "move HEAD");
        var head = FixtureRepo.Run(repo.Root.Path, "git", "rev-parse", "HEAD").Trim();

        using (BreakGitStatus(repo))
            await Assert.ThrowsAsync<FuseException>(() => tracker.SyncAsync([], TestContext.Current.CancellationToken));

        Assert.Equal(before, tracker.Head);
        Assert.Equal([repo.PathOf("Lib/Extra.cs")], tracker.Changed);
        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(await tracker.SyncAsync([], TestContext.Current.CancellationToken));
        Assert.Equal($"HEAD moved to {head[..12]}", reevaluate.Trigger);
        Assert.Equal(head, tracker.Head);
        Assert.Equal([repo.PathOf("Lib/Extra.cs")], tracker.Changed);
    }

    [Fact]
    public async Task A_reseed_that_fails_after_a_watcher_error_keeps_the_changes_and_the_next_sync_retries()
    {
        using var repo = Repo();
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");
        await tracker.SyncAsync([repo.PathOf("Lib/Calc.cs")], TestContext.Current.CancellationToken);
        watched.RecordError("Too many changes at once in directory");

        using (BreakGitStatus(repo))
            await Assert.ThrowsAsync<FuseException>(() => tracker.SyncAsync([], TestContext.Current.CancellationToken));

        Assert.Equal([repo.PathOf("Lib/Calc.cs")], tracker.Changed);
        // The watcher's error was taken by the sync that failed; the next one still knows the events are not the whole change.
        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(await tracker.SyncAsync([], TestContext.Current.CancellationToken));
        Assert.Equal("watcher error: Too many changes at once in directory", reevaluate.Trigger);
        Assert.Equal([repo.PathOf("Lib/Calc.cs")], tracker.Changed);
    }

    /// <summary>
    ///     Makes <c>git status</c> fail until disposed, by overwriting the index with bytes git cannot read. Resolving HEAD
    ///     reads the refs and the commit, not the index, so only the reseed fails.
    /// </summary>
    private static IndexRestorer BreakGitStatus(FixtureRepo repo)
    {
        var index = Path.Combine(repo.Root.Path, ".git", "index");
        var saved = File.ReadAllBytes(index);
        File.WriteAllBytes(index, "not an index"u8.ToArray());
        return new IndexRestorer(index, saved);
    }

    /// <summary>Puts the saved index back.</summary>
    private sealed class IndexRestorer(string path, byte[] saved) : IDisposable
    {
        public void Dispose() => File.WriteAllBytes(path, saved);
    }

    /// <summary><paramref name="count"/> sources that no commit and no file on disk has.</summary>
    private static List<RepoPath> Generated(FixtureRepo repo, int count) =>
        [.. Enumerable.Range(0, count).Select(i => repo.PathOf($"Lib/Generated/G{i}.cs"))];

    private static FixtureRepo Repo() => FixtureRepo.CreateEmpty(new Dictionary<string, string>
    {
        ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
        ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
    });

    private static async Task<ChangeTracker> StartAsync(FixtureRepo repo, WatchedPaths watched)
    {
        var tracker = new ChangeTracker(repo.Root, watched);
        await tracker.InitializeAsync(TestContext.Current.CancellationToken);
        return tracker;
    }
}
