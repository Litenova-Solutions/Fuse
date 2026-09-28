using Fuse.Cli;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     One real build or test run at a time in a repository. Two at once make MSBuild fail on a file the other is
///     holding, or on a source file both are writing, and the failure reads like a broken repository rather than like two
///     clients that collided. These cases run the real executable, because the lock is a file the operating system
///     arbitrates between processes, which an in-process test could not show.
/// </summary>
public class BuildLockTests
{
    private static readonly string[] Collisions = ["MSB3021", "MSB3026", "MSB3027", "CS2012"];

    // Calc.Add returns a + b and CalcTests.Adds asserts Add(1, 2) == 3. Adding one more compiles and fails that test.
    private const string BreakAdd = "public int Add(int a, int b) => a + b;";
    private const string BreakAddAs = "public int Add(int a, int b) => a + b + 1;";

    [Fact]
    public async Task Three_builds_at_once_all_succeed()
    {
        using var repo = Create();
        try
        {
            // The lock's own property: the three clients take turns, and no two of them fail over a file one of them is
            // holding. Those are the three MSB ids, which is what two builds copying the same file produce.
            var clients = Enumerable.Range(0, 3).Select(_ => FuseProcess.Start(repo.Path, "build", "Lib/Lib.csproj", "-nr:false")).ToList();
            var results = await Task.WhenAll(clients.Select(c => c.WaitAsync(TimeSpan.FromMinutes(5))));

            foreach (var (_, stdout, stderr) in results)
                AssertNoCollisions(stdout + stderr);
            Assert.Single(results.Select(r => r.ExitCode).Distinct());
            Assert.Equal(0, results[0].ExitCode);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_killed_build_does_not_block_the_next_client()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            // -t:Rebuild makes the build long enough to be caught holding the lock.
            await using var killed = FuseProcess.Start(repo.Path, "build", "-t:Rebuild");
            Assert.True(await WaitForLockAsync(repo, TimeSpan.FromMinutes(2)), "the build never took the lock");

            killed.Kill();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var next = await FuseProcess.RunAsync(repo.Path, null, "build");

            Assert.Equal(0, next.ExitCode);
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"the next client waited {watch.Elapsed}, which reads as blocked");
            AssertNoCollisions(next.Stdout + next.Stderr);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Two_test_runs_at_once_both_report_the_same_result()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", BreakAdd, BreakAddAs);
            var clients = Enumerable.Range(0, 2).Select(_ => FuseProcess.Start(repo.Path, "test")).ToList();
            var results = await Task.WhenAll(clients.Select(c => c.WaitAsync(TimeSpan.FromMinutes(6))));

            Assert.All(results, r => Assert.Equal(results[0].ExitCode, r.ExitCode));
            Assert.All(results, r => Assert.Contains("CalcTests.Adds", r.Stdout + r.Stderr, StringComparison.Ordinal));
            foreach (var (_, stdout, stderr) in results)
                AssertNoCollisions(stdout + stderr);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_build_behind_a_test_run_succeeds()
    {
        using var repo = Create();
        try
        {
            repo.Replace("Lib/Calc.cs", BreakAdd, BreakAddAs);
            await using var test = FuseProcess.Start(repo.Path, "test");
            var build = FuseProcess.Start(repo.Path, "build", "-nr:false");

            var testResult = await test.WaitAsync(TimeSpan.FromMinutes(6));
            var buildResult = await build.WaitAsync(TimeSpan.FromMinutes(5));

            Assert.Equal(0, buildResult.ExitCode);
            AssertNoCollisions(buildResult.Stdout + buildResult.Stderr);
            Assert.Contains("CalcTests.Adds", testResult.Stdout + testResult.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task The_waiting_line_appears_only_when_the_client_actually_waited()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var alone = await FuseProcess.RunAsync(repo.Path, null, "build");
            Assert.Equal(0, alone.ExitCode);
            Assert.DoesNotContain(BuildLock.Waited, alone.Stderr, StringComparison.Ordinal);

            // The lock the test takes is the same lock a `fuse test` takes, so the build below waits for the same reason.
            using var held = BuildLock.Acquire(repo.Root);
            Assert.True(held.Held, "the test could not take the lock, so the case would pass for the wrong reason");

            await using var waiting = FuseProcess.Start(repo.Path, "build");
            var announced = await WaitForAsync(() => waiting.StderrSoFar.Contains(BuildLock.Waited, StringComparison.Ordinal), TimeSpan.FromMinutes(2));
            Assert.True(announced, $"a client that waited said nothing; its stderr was: {waiting.StderrSoFar}");

            held.Dispose();
            var result = await waiting.WaitAsync(TimeSpan.FromMinutes(5));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, result.Stderr.Split('\n').Count(l => l.Contains(BuildLock.Waited, StringComparison.Ordinal)));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_client_whose_lock_file_cannot_be_opened_still_builds()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            // The lock is protection, not a prerequisite. A directory where the lock file belongs cannot be opened as a
            // file, which is the shape of an unwritable state directory, and the client still builds.
            Directory.CreateDirectory(Path.Combine(repo.Root.StateDirectory, "build.lock"));
            using var blocked = BuildLock.Acquire(repo.Root);
            Assert.False(blocked.Held);
            Assert.NotNull(blocked.Reason);

            var result = await FuseProcess.RunAsync(repo.Path, null, "build");
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    /// <summary>
    ///     A fixture whose builds do not go through the shared compiler server. What is under test is the lock: two csc
    ///     runs routed through VBCSCompiler can collide on the same obj file with CS2012 even when they are serialized, and
    ///     that is a different defect, recorded in decisions.md, which the lock does not cover. The build tests also pass
    ///     <c>-nr:false</c>, because a finished build's MSBuild node keeps its output assemblies open for reading and the
    ///     *next* build then cannot overwrite them, which again is not two clients colliding.
    /// </summary>
    private static FixtureRepo Create()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <UseSharedCompilation>false</UseSharedCompilation>\n  </PropertyGroup>\n</Project>\n");
        return repo;
    }

    private static void AssertNoCollisions(string output)
    {
        foreach (var id in Collisions)
            Assert.DoesNotContain(id, output, StringComparison.Ordinal);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }

        return condition();
    }

    /// <summary>True once some process holds the repository's lock file exclusively.</summary>
    private static async Task<bool> WaitForLockAsync(FixtureRepo repo, TimeSpan timeout)
    {
        var path = Path.Combine(repo.Root.StateDirectory, "build.lock");
        return await WaitForAsync(() =>
        {
            try
            {
                using var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }, timeout);
    }
}
