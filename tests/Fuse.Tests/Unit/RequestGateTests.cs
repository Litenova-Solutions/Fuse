using Fuse.Engine;

namespace Fuse.Tests.Unit;

/// <summary>
///     The request lock tells each request what held it longest while the request waited, which the phase line writes as
///     <c>gateHolder</c> so the evals can say what a long gate wait was spent behind.
/// </summary>
public class RequestGateTests
{
    [Fact]
    public async Task A_request_that_does_not_wait_names_no_holder()
    {
        using var gate = new RequestGate();

        var holder = await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);

        Assert.Null(holder);
        gate.Exit();
    }

    [Fact]
    public async Task A_request_that_waits_names_what_held_the_lock()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("PlanAffectedTests", TestContext.Current.CancellationToken);

        var waiting = gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        gate.Exit();

        Assert.Equal("PlanAffectedTests", await waiting);
        gate.Exit();
    }

    [Fact]
    public async Task A_request_that_waits_behind_two_holders_names_the_one_that_held_the_lock_longest()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);
        var second = gate.EnterAsync("PlanAffectedTests", TestContext.Current.CancellationToken);
        var third = gate.EnterAsync("CheckChanges", TestContext.Current.CancellationToken);

        // The first holder leaves at once; the second holds the lock far longer while the third waits behind both.
        gate.Exit();
        Assert.Equal("CheckFiles", await second);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        gate.Exit();

        Assert.Equal("PlanAffectedTests", await third);
        gate.Exit();
    }

    [Fact]
    public async Task A_wait_that_is_cancelled_does_not_hold_the_lock()
    {
        using var gate = new RequestGate();
        await gate.EnterAsync(RequestGate.Preload, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var cancelled = gate.EnterAsync("CheckFiles", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Exit();

        // The next request takes the lock at once, and the cancelled wait left nothing behind that it would be credited to.
        Assert.Null(await gate.EnterAsync("CheckFiles", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        gate.Exit();
    }
}
