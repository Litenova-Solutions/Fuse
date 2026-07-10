using System.Diagnostics;
using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// N3 (finding 5): the background semantic upgrade is supervised, not fire-and-forget. It is deduped per root, its
// failures are logged rather than swallowed, and shutdown cancels and drains in-flight jobs so none is orphaned.
public sealed class SemanticUpgradeSupervisorTests
{
    [Fact]
    public async Task Schedule_dedupes_per_root()
    {
        await using var supervisor = new SemanticUpgradeSupervisor();
        var gate = new TaskCompletionSource();

        var first = supervisor.Schedule("root-a", _ => gate.Task);
        var second = supervisor.Schedule("root-a", _ => gate.Task);

        Assert.True(first);
        Assert.False(second); // already running for this root
        gate.SetResult();
    }

    [Fact]
    public async Task Dispose_cancels_and_drains_in_flight_jobs()
    {
        var observedCancellation = new TaskCompletionSource<bool>();
        var supervisor = new SemanticUpgradeSupervisor();
        supervisor.Schedule("root-b", async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.TrySetResult(true);
                throw;
            }
        });

        Assert.True(supervisor.HasRunning);
        await supervisor.DisposeAsync();

        // The in-flight job observed cancellation and the supervisor reports nothing running after the drain.
        Assert.True(await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(supervisor.HasRunning);
    }

    [Fact]
    public async Task Schedule_after_dispose_is_refused()
    {
        var supervisor = new SemanticUpgradeSupervisor();
        await supervisor.DisposeAsync();
        Assert.False(supervisor.Schedule("root-c", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task A_failing_job_is_logged_not_swallowed()
    {
        var logged = new List<string>();
        var done = new TaskCompletionSource();
        await using var supervisor = new SemanticUpgradeSupervisor(m => { logged.Add(m); done.TrySetResult(); });
        supervisor.Schedule("root-d", _ => throw new InvalidOperationException("boom"));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(logged, m => m.Contains("root-d") && m.Contains("boom"));
    }
}
