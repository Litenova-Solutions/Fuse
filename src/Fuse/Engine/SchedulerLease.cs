namespace Fuse.Engine;

/// <summary>
///     One reader slot of the <see cref="Scheduler"/>, held from dequeue until the request is answered. Disposing
///     it records how long the request ran, which becomes the lane's recent median the admission estimate uses,
///     and admits the next waiter.
/// </summary>
internal sealed class SchedulerLease : IDisposable
{
    private readonly Scheduler _scheduler;
    private int _disposed;

    internal SchedulerLease(Scheduler scheduler, SchedulerLane lane, string holder, TimeSpan queueTime, string? gateHolder, long dequeuedAt)
    {
        _scheduler = scheduler;
        Lane = lane;
        Holder = holder;
        QueueTime = queueTime;
        GateHolder = gateHolder;
        DequeuedAt = dequeuedAt;
    }

    /// <summary>The lane the request ran in.</summary>
    public SchedulerLane Lane { get; }

    /// <summary>What the request holds the reader as, as a phase line names it.</summary>
    public string Holder { get; }

    /// <summary>How long the request waited in the queue before a reader took it; its wait and analyzer budget start at dequeue, after this time.</summary>
    public TimeSpan QueueTime { get; }

    /// <summary>What held a reader longest while this request waited, or null when it did not wait behind a holder.</summary>
    public string? GateHolder { get; }

    /// <summary>When a reader took the request, by <see cref="System.Diagnostics.Stopwatch"/>.</summary>
    internal long DequeuedAt { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _scheduler.Release(this);
    }
}
