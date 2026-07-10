using Fuse.Cli.Services;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S1 step 3: the watcher coalesces raw filesystem events to the net change per path over a debounce window, so
// the resident workspace applies one update per file. These tests pin the coalescing rules directly on the
// accumulator (no filesystem).
public sealed class WorkspaceFileChangeSetTests
{
    [Fact]
    public void A_single_change_is_reported_as_is()
    {
        var set = new WorkspaceFileChangeSet();
        set.Add(FileChangeKind.Changed, "/repo/A.cs");

        var change = Assert.Single(set.Drain());
        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal("/repo/A.cs", change.FullPath);
    }

    [Fact]
    public void Create_then_delete_cancels()
    {
        var set = new WorkspaceFileChangeSet();
        set.Add(FileChangeKind.Created, "/repo/Temp.cs");
        set.Add(FileChangeKind.Deleted, "/repo/Temp.cs");

        Assert.Empty(set.Drain());
    }

    [Fact]
    public void Delete_then_create_becomes_changed()
    {
        var set = new WorkspaceFileChangeSet();
        set.Add(FileChangeKind.Deleted, "/repo/A.cs");
        set.Add(FileChangeKind.Created, "/repo/A.cs");

        var change = Assert.Single(set.Drain());
        Assert.Equal(FileChangeKind.Changed, change.Kind);
    }

    [Fact]
    public void Create_then_change_stays_created()
    {
        var set = new WorkspaceFileChangeSet();
        set.Add(FileChangeKind.Created, "/repo/A.cs");
        set.Add(FileChangeKind.Changed, "/repo/A.cs");

        var change = Assert.Single(set.Drain());
        Assert.Equal(FileChangeKind.Created, change.Kind);
    }

    [Fact]
    public void Latest_wins_for_repeated_changes_and_change_then_delete()
    {
        var set = new WorkspaceFileChangeSet();
        set.Add(FileChangeKind.Changed, "/repo/A.cs");
        set.Add(FileChangeKind.Changed, "/repo/A.cs");
        set.Add(FileChangeKind.Deleted, "/repo/A.cs");

        var change = Assert.Single(set.Drain());
        Assert.Equal(FileChangeKind.Deleted, change.Kind);
    }

    [Fact]
    public void Count_reflects_distinct_paths_and_drain_clears()
    {
        var set = new WorkspaceFileChangeSet();
        set.Add(FileChangeKind.Changed, "/repo/A.cs");
        set.Add(FileChangeKind.Changed, "/repo/B.cs");
        Assert.Equal(2, set.Count);

        Assert.Equal(2, set.Drain().Count);
        Assert.Equal(0, set.Count);
        Assert.Empty(set.Drain());
    }
}
