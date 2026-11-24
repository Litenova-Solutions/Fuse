using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class DagBuildLockTests
{
    [Fact]
    public void Closure_key_is_order_independent_and_canonical()
    {
        var key1 = BuildLock.ClosureKey(["/repo/src/B/B.csproj", "/repo/src/A/A.csproj"]);
        var key2 = BuildLock.ClosureKey(["/repo/src/A/A.csproj", "/repo/src/B/B.csproj", "/repo/src/A/A.csproj"]);

        Assert.Equal(16, key1.Length);
        Assert.Equal(key1, key2);
        Assert.Equal("root", BuildLock.ClosureKey([]));
    }

    [Fact]
    public void Disjoint_project_closures_acquire_locks_concurrently_without_waiting()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["ProjA/ProjA.csproj"] = "<Project />",
            ["ProjB/ProjB.csproj"] = "<Project />",
        });

        var waited = false;
        using var lockA = BuildLock.AcquireForProjects(repo.Root, [repo.Full("ProjA/ProjA.csproj")], () => waited = true);
        using var lockB = BuildLock.AcquireForProjects(repo.Root, [repo.Full("ProjB/ProjB.csproj")], () => waited = true);

        Assert.True(lockA.IsHeld);
        Assert.True(lockB.IsHeld);
        Assert.False(waited);
    }

    [Fact]
    public void Overlapping_project_closures_serialize()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Shared/Shared.csproj"] = "<Project />",
            ["AppA/AppA.csproj"] = "<Project />",
            ["AppB/AppB.csproj"] = "<Project />",
        });

        var closureA = new[] { repo.Full("Shared/Shared.csproj"), repo.Full("AppA/AppA.csproj") };
        var closureB = new[] { repo.Full("AppB/AppB.csproj"), repo.Full("Shared/Shared.csproj") };

        using (var held = BuildLock.AcquireForProjects(repo.Root, closureA))
        {
            Assert.True(held.IsHeld);
            Assert.False(BuildLock.TryAcquireForProjects(repo.Root, closureB, out var contended));
            Assert.Null(contended);
        }

        Assert.True(BuildLock.TryAcquireForProjects(repo.Root, closureB, out var acquired));
        using (acquired)
        {
            Assert.NotNull(acquired);
            Assert.True(acquired.IsHeld);
        }
    }

    [Fact]
    public void Whole_solution_lock_blocks_closure_lock()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Lib.csproj"] = "<Project />",
        });

        using (var whole = BuildLock.Acquire(repo.Root))
        {
            Assert.True(whole.IsHeld);
            Assert.False(BuildLock.TryAcquireForProjects(repo.Root, [repo.Full("Lib/Lib.csproj")], out var closure));
            Assert.Null(closure);
        }

        Assert.True(BuildLock.TryAcquireForProjects(repo.Root, [repo.Full("Lib/Lib.csproj")], out var after));
        after?.Dispose();
    }

    [Fact]
    public void Acquire_isolated_creates_distinct_artifacts_directories_under_state_directory()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });

        using var iso1 = BuildLock.AcquireIsolated(repo.Root);
        using var iso2 = BuildLock.AcquireIsolated(repo.Root);

        Assert.True(iso1.IsHeld);
        Assert.True(iso2.IsHeld);
        Assert.NotNull(iso1.ArtifactsPath);
        Assert.NotNull(iso2.ArtifactsPath);
        Assert.NotEqual(iso1.ArtifactsPath, iso2.ArtifactsPath);
        Assert.StartsWith(repo.Root.StateDirectory, iso1.ArtifactsPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acquire_for_projects_or_isolate_uses_isolated_artifacts_on_contention()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        var project = repo.Full("Lib/Lib.csproj");

        using var held = BuildLock.AcquireForProjects(repo.Root, [project]);
        using var isolated = BuildLock.AcquireForProjectsForClient(repo.Root, [project], parallelArtifacts: false, isolateOnContention: true);

        Assert.True(held.IsHeld);
        Assert.Null(held.ArtifactsPath);
        Assert.True(isolated.IsHeld);
        Assert.NotNull(isolated.ArtifactsPath);
    }

    [Fact]
    public void Build_traversal_project_xml_references_all_projects_and_escapes_paths()
    {
        var xml = BuildOperation.BuildTraversalProjectXml(["C:/repo/A&B/A.csproj", "C:/repo/C/C.csproj"], continueOnError: true);

        Assert.Contains("<ProjectReference Include=\"C:/repo/A&amp;B/A.csproj\" />", xml, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"C:/repo/C/C.csproj\" />", xml, StringComparison.Ordinal);
        Assert.Contains("BuildInParallel=\"true\"", xml, StringComparison.Ordinal);
        Assert.Contains("ContinueOnError=\"ErrorAndContinue\"", xml, StringComparison.Ordinal);
    }
}
