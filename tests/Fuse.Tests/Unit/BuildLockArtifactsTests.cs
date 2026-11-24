using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class BuildLockArtifactsTests
{
    [Fact]
    public void Released_isolated_lock_deletes_its_slot()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        var slot = AcquireSlot(repo);

        Assert.False(Directory.Exists(slot));
    }

    [Fact]
    public void Worker_key_slot_is_kept_when_its_lock_is_released()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        string? slot;
        using (var shared = BuildLock.AcquireIsolated(repo.Root, workerKey: "worker-1"))
        {
            Assert.True(shared.IsHeld);
            slot = shared.ArtifactsPath;
            Assert.NotNull(slot);
        }

        Assert.True(Directory.Exists(slot));
    }

    [Fact]
    public void Stale_unheld_slot_is_pruned_on_acquisition()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        var stale = MakeSlot(repo, "stale", old: true);

        using var live = BuildLock.AcquireIsolated(repo.Root);

        Assert.True(live.IsHeld);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(live.ArtifactsPath!));
    }

    [Fact]
    public void Stale_slot_without_a_lock_file_is_pruned_on_acquisition()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        var parent = ArtifactsParent(repo);
        var stale = Path.Combine(parent, "nolock");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "Lib.dll"), "output");
        Age(stale);

        using var live = BuildLock.AcquireIsolated(repo.Root);

        Assert.True(live.IsHeld);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void Fresh_unheld_slot_is_kept()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        var fresh = MakeSlot(repo, "fresh", old: false);

        using var live = BuildLock.AcquireIsolated(repo.Root);

        Assert.True(live.IsHeld);
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Held_slot_is_kept_even_when_old()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        using var held = BuildLock.AcquireIsolated(repo.Root);
        Assert.True(held.IsHeld);
        Age(held.ArtifactsPath!);

        using var live = BuildLock.AcquireIsolated(repo.Root);

        Assert.True(live.IsHeld);
        Assert.True(Directory.Exists(held.ArtifactsPath!));
    }

    [Fact]
    public void Prune_deletes_at_most_max_slots_pruned_per_acquisition()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        for (var i = 0; i < BuildLock.MaxSlotsPruned + 2; i++)
            MakeSlot(repo, $"stale-{i:000}", old: true);

        using var live = BuildLock.AcquireIsolated(repo.Root);

        Assert.True(live.IsHeld);
        var remaining = Directory.EnumerateDirectories(ArtifactsParent(repo)).Where(d => d != live.ArtifactsPath).ToList();
        Assert.Equal(2, remaining.Count);
    }

    /// <summary>Acquires an isolated lock, releases it, and returns its slot, which releasing deletes.</summary>
    private static string AcquireSlot(FixtureRepo repo)
    {
        using var isolated = BuildLock.AcquireIsolated(repo.Root);
        Assert.True(isolated.IsHeld);
        Assert.NotNull(isolated.ArtifactsPath);
        Assert.True(Directory.Exists(isolated.ArtifactsPath));
        return isolated.ArtifactsPath;
    }

    /// <summary>An unheld slot folder under the repository's artifacts directory, with a build output file and a closed lock file.</summary>
    private static string MakeSlot(FixtureRepo repo, string name, bool old)
    {
        var slot = Path.Combine(ArtifactsParent(repo), name);
        Directory.CreateDirectory(slot);
        File.WriteAllText(Path.Combine(slot, "Lib.dll"), "output");
        File.WriteAllText(Path.Combine(slot, "artifacts.lock"), "");
        if (old)
            Age(slot);
        return slot;
    }

    private static string ArtifactsParent(FixtureRepo repo) => Path.Combine(repo.Root.StateDirectory, "artifacts");

    /// <summary>Makes <paramref name="directory"/> stale: older than a slot may be before an acquisition prunes it.</summary>
    private static void Age(string directory) =>
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - BuildLock.StaleSlotAge - TimeSpan.FromDays(1));
}
