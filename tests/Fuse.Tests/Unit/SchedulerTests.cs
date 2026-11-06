using Fuse.Engine;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     The scheduler admits as many readers as its pool holds, in lane order, refuses a request that will not
///     start before its wait ends, shares an identical check in flight, and cancels background work of an older
///     generation when a newer one is published.
/// </summary>
public class SchedulerTests
{
    [Fact]
    public void The_reader_pool_is_the_core_count_minus_one_for_the_writer()
    {
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), Scheduler.DefaultReaderCount);
        using var scheduler = new Scheduler(Scheduler.DefaultReaderCount);
        Assert.Equal(Scheduler.DefaultReaderCount, scheduler.ReaderCount);
    }

    [Fact]
    public async Task Readers_run_up_to_the_pool_and_the_next_request_waits()
    {
        using var scheduler = new Scheduler(2);
        var first = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
        var second = await scheduler.EnterAsync(SchedulerLane.Command, "CheckChanges", 0, TestContext.Current.CancellationToken);
        var third = scheduler.EnterAsync(SchedulerLane.Command, "PlanAllTests", 0, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, scheduler.RunningCount);
        Assert.Equal(1, scheduler.QueuedCount);

        first.Dispose();
        var admitted = await third.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("CheckChanges", admitted.GateHolder);
        admitted.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task Interactive_requests_run_before_commands_and_background_work()
    {
        using var scheduler = new Scheduler(1);
        var holder = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);

        var background = scheduler.EnterAsync(SchedulerLane.Background, "Preload", 0, TestContext.Current.CancellationToken);
        var command = scheduler.EnterAsync(SchedulerLane.Command, "PlanAllTests", 0, TestContext.Current.CancellationToken);
        var interactive = scheduler.EnterAsync(SchedulerLane.Interactive, "CheckChanges", 0, TestContext.Current.CancellationToken);
        holder.Dispose();

        var first = await interactive.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("CheckFiles", first.GateHolder);
        Assert.False(command.IsCompleted);
        Assert.False(background.IsCompleted);
        first.Dispose();

        var second = await command.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(background.IsCompleted);
        second.Dispose();

        var third = await background.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        third.Dispose();
    }

    [Fact]
    public async Task A_request_that_will_not_start_before_its_wait_ends_is_refused_with_its_position_and_estimate()
    {
        using var scheduler = new Scheduler(1);
        scheduler.RecordDuration(SchedulerLane.Command, TimeSpan.FromSeconds(10));
        var holder = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
        try
        {
            var busy = await Assert.ThrowsAsync<SchedulerBusyException>(() =>
                scheduler.EnterAsync(SchedulerLane.Command, "CheckChanges", waitMs: 1000, TestContext.Current.CancellationToken));
            Assert.Equal(1, busy.Position);
            Assert.Equal(10000, busy.EstimatedWaitMs);
            Assert.Equal(0, scheduler.QueuedCount);
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public async Task A_request_with_no_median_yet_queues_instead_of_being_refused()
    {
        using var scheduler = new Scheduler(1);
        var holder = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
        var queued = scheduler.EnterAsync(SchedulerLane.Command, "CheckChanges", waitMs: 1, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);
        holder.Dispose();
        (await queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    public void Lanes_follow_the_request_kind()
    {
        Assert.Equal(SchedulerLane.Interactive, Scheduler.LaneFor(new EngineRequest.CheckFiles(["a.cs"], WaitForLoad: true) { LeaveOutCompilationWide = true }));
        Assert.Equal(SchedulerLane.Interactive, Scheduler.LaneFor(new EngineRequest.CheckChanges(WaitForLoad: true) { IsStopping = true }));
        Assert.Equal(SchedulerLane.Command, Scheduler.LaneFor(new EngineRequest.CheckFiles(["a.cs"], WaitForLoad: true)));
        Assert.Equal(SchedulerLane.Command, Scheduler.LaneFor(new EngineRequest.CheckChanges(WaitForLoad: true)));
        Assert.Equal(SchedulerLane.Command, Scheduler.LaneFor(new EngineRequest.PlanAllTests()));
    }

    [Fact]
    public async Task Identical_checks_in_flight_share_one_computation()
    {
        using var scheduler = new Scheduler(2);
        var runs = 0;
        async Task<EngineResponse> Work(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(150, token);
            return new EngineResponse.Acknowledged();
        }

        var answers = await Task.WhenAll(
            scheduler.RunCoalescedAsync("same", Work, TestContext.Current.CancellationToken),
            scheduler.RunCoalescedAsync("same", Work, TestContext.Current.CancellationToken));

        Assert.Equal(1, runs);
        Assert.All(answers, a => Assert.IsType<EngineResponse.Acknowledged>(a));
    }

    [Fact]
    public async Task One_waiter_giving_up_does_not_cancel_the_work_the_others_wait_for()
    {
        using var scheduler = new Scheduler(1);
        using var givingUp = new CancellationTokenSource();
        var cancelled = false;
        async Task<EngineResponse> Work(CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return new EngineResponse.Acknowledged();
        }

        var staying = scheduler.RunCoalescedAsync("shared", Work, TestContext.Current.CancellationToken);
        var leaving = scheduler.RunCoalescedAsync("shared", Work, givingUp.Token);
        await givingUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        Assert.IsType<EngineResponse.Acknowledged>(await staying.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(cancelled);
    }

    [Fact]
    public async Task A_waiter_that_joins_after_the_last_waiter_gave_up_starts_the_work_again()
    {
        using var scheduler = new Scheduler(1);
        using var givingUp = new CancellationTokenSource();
        var runs = 0;
        async Task<EngineResponse> Work(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(300, token);
            return new EngineResponse.Acknowledged();
        }

        var leaving = scheduler.RunCoalescedAsync("shared", Work, givingUp.Token);
        await givingUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        var joined = await scheduler.RunCoalescedAsync("shared", Work, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.IsType<EngineResponse.Acknowledged>(joined);
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Publishing_a_newer_generation_cancels_background_work_of_the_older_one()
    {
        using var scheduler = new Scheduler(1);
        var older = scheduler.BackgroundToken;
        scheduler.PublishGeneration(1);

        Assert.True(older.IsCancellationRequested);
        var current = scheduler.BackgroundToken;
        Assert.False(current.IsCancellationRequested);
        scheduler.PublishGeneration(1);
        Assert.False(current.IsCancellationRequested);
        scheduler.PublishGeneration(2);
        Assert.True(current.IsCancellationRequested);
    }
}
