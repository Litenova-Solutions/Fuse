using Fuse.Repo;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A check of every change confirms the changed set against git when the watcher has reported nothing, so a write
///     the watcher reports late, as a shell command's write on macOS can be, is still checked.
/// </summary>
public class DiskConfirmationTests
{
    [Fact]
    public async Task A_confirming_sync_finds_a_write_no_one_reported()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
            ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
        });
        using var tracker = new ChangeTracker(repo.Root, new WatchedPaths(repo.Root));
        await tracker.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(tracker.Changed);

        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");
        var patch = Assert.IsType<SyncResult.Patch>(await tracker.SyncAsync([], TestContext.Current.CancellationToken, confirmWithDisk: true));

        Assert.Contains(repo.PathOf("Lib/Calc.cs"), patch.Paths);
        Assert.Equal([repo.PathOf("Lib/Calc.cs")], tracker.Changed);
    }

    [Fact]
    public async Task A_confirming_sync_keeps_every_changed_file_it_already_held()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
            ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
            ["Lib/Other.cs"] = "namespace Lib;\n\npublic class Other {}\n",
        });
        using var tracker = new ChangeTracker(repo.Root, new WatchedPaths(repo.Root));
        await tracker.InitializeAsync(TestContext.Current.CancellationToken);
        repo.Replace("Lib/Calc.cs", "public class Calc {}", "public class Calc { public int A => 1; }");
        await tracker.SyncAsync([repo.PathOf("Lib/Calc.cs")], TestContext.Current.CancellationToken);

        repo.Replace("Lib/Other.cs", "public class Other {}", "public class Other { public int B => 2; }");
        await tracker.SyncAsync([], TestContext.Current.CancellationToken, confirmWithDisk: true);

        Assert.Equal([repo.PathOf("Lib/Calc.cs"), repo.PathOf("Lib/Other.cs")], tracker.Changed.OrderBy(p => p.Relative, StringComparer.Ordinal));
    }
}
