using Fuse.Engine;

namespace Fuse.Tests.Unit;

/// <summary>
///     A priority waiter runs before normal waiters that queued first, in arrival order among priority waiters, and
///     still waits for the holder that is already running, so a registration never begins or ends mid-check.
/// </summary>
public class GatePriorityTests
{
    [Fact]
    public async Task A_priority_waiter_runs_before_a_queued_normal_waiter()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);

        var normal = gate.EnterAsync("PlanAffectedTests", TestContext.Current.CancellationToken);
        var priority = gate.EnterPriorityAsync("BeginSession", TestContext.Current.CancellationToken);
        gate.Exit();

        Assert.Equal("CheckFiles", await priority.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(normal.IsCompleted);
        gate.Exit();

        Assert.Equal("BeginSession", await normal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }

    [Fact]
    public async Task Priority_waiters_run_in_arrival_order_before_normal_waiters()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);

        var normal = gate.EnterAsync("CheckChanges", TestContext.Current.CancellationToken);
        var first = gate.EnterPriorityAsync("BeginSession", TestContext.Current.CancellationToken);
        var second = gate.EnterPriorityAsync("Status", TestContext.Current.CancellationToken);
        gate.Exit();

        Assert.Equal("CheckFiles", await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(second.IsCompleted);
        Assert.False(normal.IsCompleted);
        gate.Exit();

        Assert.Equal("BeginSession", await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(normal.IsCompleted);
        gate.Exit();

        Assert.Equal("Status", await normal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }

    [Fact]
    public async Task A_priority_waiter_still_waits_for_the_current_holder()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);

        var priority = gate.EnterPriorityAsync("BeginSession", TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(priority.IsCompleted);
        Assert.Equal("CheckFiles", gate.CurrentHolder);
        gate.Exit();

        Assert.Equal("CheckFiles", await priority.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("BeginSession", gate.CurrentHolder);
        gate.Exit();
        Assert.Null(gate.CurrentHolder);
    }

    [Fact]
    public async Task A_cancelled_priority_waiter_leaves_the_normal_waiter_next()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var cancelled = gate.EnterPriorityAsync("BeginSession", cancel.Token);
        var normal = gate.EnterAsync("CheckChanges", TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Exit();

        Assert.Equal("CheckFiles", await normal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }
}
