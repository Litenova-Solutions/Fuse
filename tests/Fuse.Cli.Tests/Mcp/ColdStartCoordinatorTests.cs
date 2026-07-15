using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R27: the cold read is bounded. The syntax build runs in the background against a deadline; a read that outruns
// the deadline gets a building_syntax signal while the build continues, and a later read joins the same build.
public sealed class ColdStartCoordinatorTests
{
    [Fact]
    public async Task FastBuild_CompletesWithinDeadline_ReturnsTrue()
    {
        var coordinator = new ColdStartCoordinator();

        var built = await coordinator.BuildWithDeadlineAsync(
            NewRoot(), _ => Task.CompletedTask, deadlineMilliseconds: 2000, CancellationToken.None);

        Assert.True(built);
    }

    [Fact]
    public async Task SlowBuild_ExceedsDeadline_ReturnsFalse_ThenBuildCompletes()
    {
        var coordinator = new ColdStartCoordinator();
        var root = NewRoot();
        var gate = new TaskCompletionSource();

        var built = await coordinator.BuildWithDeadlineAsync(
            root, _ => gate.Task, deadlineMilliseconds: 50, CancellationToken.None);

        Assert.False(built); // did not finish within the deadline...
        Assert.True(coordinator.HasInFlightBuild(root)); // ...but it is still running in the background.

        gate.SetResult();
        await coordinator.BuildWithDeadlineAsync(root, _ => gate.Task, 50, CancellationToken.None); // drains completion.

        var second = await coordinator.BuildWithDeadlineAsync(
            root, _ => Task.CompletedTask, deadlineMilliseconds: 2000, CancellationToken.None);
        Assert.True(second); // a later read finds the build done.
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneBuild()
    {
        var coordinator = new ColdStartCoordinator();
        var root = NewRoot();
        var gate = new TaskCompletionSource();
        var runs = 0;
        Func<CancellationToken, Task> build = async _ =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        };

        var first = coordinator.BuildWithDeadlineAsync(root, build, 50, CancellationToken.None);
        var second = coordinator.BuildWithDeadlineAsync(root, build, 50, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.False(r)); // both outran the 50ms deadline.
        Assert.Equal(1, runs); // the build ran exactly once (deduped per root).
        gate.SetResult();
    }

    [Fact]
    public void DeadlineMilliseconds_HonorsEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable(ColdStartCoordinator.DeadlineEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ColdStartCoordinator.DeadlineEnvVar, "1234");
            Assert.Equal(1234, ColdStartCoordinator.DeadlineMilliseconds());
            Environment.SetEnvironmentVariable(ColdStartCoordinator.DeadlineEnvVar, null);
            Assert.Equal(ColdStartCoordinator.DefaultDeadlineMilliseconds, ColdStartCoordinator.DeadlineMilliseconds());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ColdStartCoordinator.DeadlineEnvVar, original);
        }
    }

    [Fact]
    public async Task BuildingSyntaxHeader_OnColdRepo_ReportsBuildingSyntaxState()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git")); // isolate the store under this root.
        try
        {
            var header = await FuseTools.FormatBuildingSyntaxHeaderAsync(root, CancellationToken.None);

            Assert.Contains("index_state: building_syntax", header, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "fuse-coldstart", Guid.NewGuid().ToString("N"));
}
