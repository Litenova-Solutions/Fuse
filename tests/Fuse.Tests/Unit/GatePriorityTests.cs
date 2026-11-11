using Fuse.Engine;

namespace Fuse.Tests.Unit;

/// <summary>
///     The gate the first evaluation and the background load hold while they choose work: a priority holder
///     runs before normal holders that queued first, in arrival order among priority holders, and still waits
///     for the holder that is already running. Priority among requests is the scheduler's lane order, in
///     <c>SchedulerTests</c>; no request waits on this gate.
/// </summary>
public class GatePriorityTests
{
    [Fact]
    public async Task A_priority_holder_runs_before_a_queued_normal_holder()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("Preload", TestContext.Current.CancellationToken);

        var normal = gate.EnterAsync("Initialization", TestContext.Current.CancellationToken);
        var priority = gate.EnterPriorityAsync("Preload", TestContext.Current.CancellationToken);
        gate.Exit();

        Assert.Equal("Preload", await priority.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(normal.IsCompleted);
        gate.Exit();

        Assert.Equal("Preload", await normal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }

    [Fact]
    public async Task Priority_holders_run_in_arrival_order_before_normal_holders()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("Preload", TestContext.Current.CancellationToken);

        var normal = gate.EnterAsync("Initialization", TestContext.Current.CancellationToken);
        var first = gate.EnterPriorityAsync("Preload", TestContext.Current.CancellationToken);
        var second = gate.EnterPriorityAsync("Preload", TestContext.Current.CancellationToken);
        gate.Exit();

        Assert.Equal("Preload", await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(second.IsCompleted);
        Assert.False(normal.IsCompleted);
        gate.Exit();

        Assert.Equal("Preload", await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(normal.IsCompleted);
        gate.Exit();

        Assert.Equal("Preload", await normal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }

    [Fact]
    public async Task A_priority_holder_still_waits_for_the_current_holder()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("Preload", TestContext.Current.CancellationToken);

        var priority = gate.EnterPriorityAsync("Initialization", TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(priority.IsCompleted);
        Assert.Equal("Preload", gate.CurrentHolder);
        gate.Exit();

        Assert.Equal("Preload", await priority.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("Initialization", gate.CurrentHolder);
        gate.Exit();
        Assert.Null(gate.CurrentHolder);
    }

    [Fact]
    public async Task A_cancelled_priority_holder_leaves_the_normal_holder_next()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("Preload", TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var cancelled = gate.EnterPriorityAsync("Initialization", cancel.Token);
        var normal = gate.EnterAsync("Preload", TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Exit();

        Assert.Equal("Preload", await normal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }
}
