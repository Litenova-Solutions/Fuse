using Fuse.Repo;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     What the file watcher reports is kept until a sync takes it, and each sync takes it once. These cases record the
///     events themselves and never start the watcher, so nothing depends on when the file system delivers an event.
/// </summary>
public class WatchedPathsTests
{
    [Fact]
    public void A_written_source_is_taken_by_the_next_sync_only()
    {
        using var repo = Repo();
        using var watched = new WatchedPaths(repo.Root);
        watched.Record(repo.Full("Lib/Calc.cs"), appearedOrVanished: false);
        watched.Record(repo.Full("Lib/Calc.cs"), appearedOrVanished: false);

        Assert.Equal([repo.Full("Lib/Calc.cs")], watched.Drain().Sources);
        Assert.Empty(watched.Drain().Sources);
    }

    [Fact]
    public void A_source_marked_dirty_is_taken_again_at_the_next_sync()
    {
        using var repo = Repo();
        using var watched = new WatchedPaths(repo.Root);
        watched.Record(repo.Full("Lib/Calc.cs"), appearedOrVanished: false);
        Assert.Single(watched.Drain().Sources);

        // A file that is still being written is read again at the next sync.
        watched.MarkDirty(repo.Full("Lib/Calc.cs"));

        Assert.Equal([repo.Full("Lib/Calc.cs")], watched.Drain().Sources);
    }

    [Fact]
    public void Project_files_are_reported_apart_from_sources_in_event_order()
    {
        using var repo = Repo();
        using var watched = new WatchedPaths(repo.Root);
        watched.Record(repo.Full("Lib/Lib.csproj"), appearedOrVanished: false);
        watched.Record(repo.Full("Directory.Build.props"), appearedOrVanished: true);
        watched.Record(repo.Full("Lib/Lib.csproj"), appearedOrVanished: false);

        var changes = watched.Drain();

        Assert.Equal([repo.Full("Lib/Lib.csproj"), repo.Full("Directory.Build.props"), repo.Full("Lib/Lib.csproj")], changes.ProjectFiles);
        Assert.Empty(changes.Sources);
        Assert.Empty(watched.Drain().ProjectFiles);
    }

    [Fact]
    public void Build_output_git_and_node_modules_are_ignored()
    {
        using var repo = Repo();
        using var watched = new WatchedPaths(repo.Root);
        string[] ignored = ["Lib/obj/Generated.cs", "Lib/bin/Release/Lib.csproj", ".git/HEAD", "web/node_modules/pkg/index.cs", "Lib/.git/x.cs"];
        foreach (var path in ignored)
            watched.Record(repo.Full(path), appearedOrVanished: true);

        var changes = watched.Drain();

        Assert.Empty(changes.Sources);
        Assert.Empty(changes.ProjectFiles);
        Assert.Empty(changes.VanishedDirectories);
        Assert.All(ignored, path => Assert.True(watched.IsIgnored(repo.Full(path)), path));
        Assert.False(watched.IsIgnored(repo.Full("Lib/Calc.cs")));
    }

    [Fact]
    public void A_directory_that_appeared_contributes_the_sources_in_it()
    {
        using var repo = Repo();
        repo.Write("Lib/Sub/Deep/New.cs", "public class New {}\n");
        repo.Write("Lib/Sub/readme.txt", "not a source\n");
        repo.Write("Lib/Sub/obj/Generated.cs", "public class Generated {}\n");
        using var watched = new WatchedPaths(repo.Root);

        watched.Record(repo.Full("Lib/Sub"), appearedOrVanished: true);
        var changes = watched.Drain();

        Assert.Equal([repo.Full("Lib/Sub/Deep/New.cs")], changes.Sources);
        Assert.Empty(changes.VanishedDirectories);
    }

    [Fact]
    public void A_path_that_is_gone_is_reported_as_a_vanished_directory()
    {
        using var repo = Repo();
        using var watched = new WatchedPaths(repo.Root);

        watched.Record(repo.Full("Lib/Gone"), appearedOrVanished: true);
        var changes = watched.Drain();

        Assert.Equal([repo.Full("Lib/Gone")], changes.VanishedDirectories);
        Assert.Empty(watched.Drain().VanishedDirectories);
    }

    [Fact]
    public void Only_a_create_delete_or_rename_of_a_directory_counts()
    {
        using var repo = Repo();
        repo.Write("Lib/Sub/New.cs", "public class New {}\n");
        repo.Write("Lib/LICENSE", "text\n");
        using var watched = new WatchedPaths(repo.Root);

        // A change event on a directory only means its listing changed, and its files report themselves.
        watched.Record(repo.Full("Lib/Sub"), appearedOrVanished: false);
        watched.Record(repo.Full("Lib/Gone"), appearedOrVanished: false);
        // An extensionless file that appeared is neither a directory nor gone.
        watched.Record(repo.Full("Lib/LICENSE"), appearedOrVanished: true);
        var changes = watched.Drain();

        Assert.Empty(changes.Sources);
        Assert.Empty(changes.VanishedDirectories);
    }

    [Fact]
    public void Watcher_errors_are_taken_once_in_order()
    {
        using var repo = Repo();
        using var watched = new WatchedPaths(repo.Root);
        watched.RecordError("first");
        watched.RecordError("second");

        Assert.Equal(["first", "second"], watched.Drain().WatcherErrors);
        Assert.Empty(watched.Drain().WatcherErrors);
    }

    private static FixtureRepo Repo() => FixtureRepo.CreateEmpty(new Dictionary<string, string>
    {
        ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
        ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
    });
}
