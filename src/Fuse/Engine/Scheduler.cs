using System.Diagnostics;
using Fuse.Protocol;

namespace Fuse.Engine;

/// <summary>
///     Admits requests to a bounded pool of readers instead of one request lock: as many readers as the machine
///     has cores minus one for the writer, ordered by lane. Interactive hooks run before commands and MCP calls,
///     which run before background work, in arrival order inside a lane. A request that will not start before its
///     wait ends is refused at once with its queue position and an estimate; an identical check already in flight
///     is shared instead of started again.
/// </summary>
/// <remarks>
///     Status, ping and session registration never enter this queue: status reads the newest generation and a
///     registration is a write <see cref="Fuse.Check.SessionEdits"/> applies under its own lock. The writer in
///     <see cref="Fuse.Workspace.RepoWorkspace"/> still serializes sync and load, so readers pin a generation and
///     bind in parallel while the next sync publishes the generation after it.
/// </remarks>
internal sealed class Scheduler : IDisposable
{
    /// <summary>How many recent durations of one lane the median is taken from.</summary>
    private const int MedianWindow = 20;

    private readonly Lock _lock = new();
    private readonly List<Waiter> _waiters = [];
    private readonly List<SchedulerLease> _running = [];
    private readonly Dictionary<SchedulerLane, List<long>> _durations = new()
    {
        [SchedulerLane.Interactive] = [],
        [SchedulerLane.Command] = [],
        [SchedulerLane.Background] = [],
    };
    private readonly Dictionary<string, Shared> _inflight = new(StringComparer.Ordinal);
    private CancellationTokenSource _background = new();
    private long _backgroundGeneration = -1;
    private bool _disposed;

    public Scheduler(int readerCount)
    {
        ReaderCount = Math.Max(1, readerCount);
    }

    /// <summary>The reader pool: one reader per core, minus one for the writer. At least one reader always runs.</summary>
    public static int DefaultReaderCount => Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>How many readers the pool holds.</summary>
    public int ReaderCount { get; }

    /// <summary>How many requests wait in the queue.</summary>
    public int QueuedCount
    {
        get
        {
            lock (_lock)
                return _waiters.Count;
        }
    }

    /// <summary>How many readers hold a request.</summary>
    public int RunningCount
    {
        get
        {
            lock (_lock)
                return _running.Count;
        }
    }

    /// <summary>What holds a reader, or null when no reader runs. A status answer names it without waiting for it.</summary>
    public string? CurrentHolder
    {
        get
        {
            lock (_lock)
                return _running.Count == 0 ? null : _running[0].Holder;
        }
    }

    /// <summary>
    ///     The lane a request waits in: an inline post-edit check and a stopping check are interactive, and every
    ///     other request the router queues is a command. Background work names its lane itself.
    /// </summary>
    public static SchedulerLane LaneFor(EngineRequest request) => request switch
    {
        EngineRequest.CheckFiles { LeaveOutCompilationWide: true } => SchedulerLane.Interactive,
        EngineRequest.CheckChanges { IsStopping: true } => SchedulerLane.Interactive,
        _ => SchedulerLane.Command,
    };

    /// <summary>The recent median duration of <paramref name="lane"/> in milliseconds, or 0 when the lane has finished nothing yet.</summary>
    public long MedianMsFor(SchedulerLane lane)
    {
        lock (_lock)
            return MedianLocked(lane);
    }

    /// <summary>Records one finished duration of <paramref name="lane"/>, keeping only the recent window the median reads.</summary>
    public void RecordDuration(SchedulerLane lane, TimeSpan duration)
    {
        lock (_lock)
            RecordLocked(lane, Math.Max(0, (long)Math.Round(duration.TotalMilliseconds)));
    }

    /// <summary>
    ///     The queue position a request of <paramref name="lane"/> would take now, and the wait that position
    ///     implies: the position times the lane's recent median. Position 0 means a reader takes it at once.
    /// </summary>
    public (int Position, long EstimatedWaitMs) EstimateFor(SchedulerLane lane)
    {
        lock (_lock)
            return EstimateLocked(lane);
    }

    /// <summary>
    ///     Waits for a reader in <paramref name="lane"/> and holds it under <paramref name="holder"/>. The wait and
    ///     the analyzer budget start when this returns, not when the request arrived.
    /// </summary>
    /// <param name="lane">The lane the request waits in.</param>
    /// <param name="holder">What is about to hold the reader, as the next waiter's phase line names it.</param>
    /// <param name="waitMs">How long the caller waits for the answer, or 0 when it named none; a request that will not start before it ends is refused.</param>
    /// <param name="cancellationToken">Stops waiting; the reader is then not held.</param>
    /// <exception cref="SchedulerBusyException">The requests ahead, at the lane's recent median, will not start this request before its wait ends.</exception>
    public async Task<SchedulerLease> EnterAsync(SchedulerLane lane, string holder, long waitMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter(lane, holder, Stopwatch.GetTimestamp());
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (position, estimate) = EstimateLocked(lane);
            if (waitMs > 0 && position > 0 && estimate > waitMs)
                throw new SchedulerBusyException(position, estimate);
            _waiters.Add(waiter);
            GrantLocked();
            if (waiter.Completion.Task.IsCompletedSuccessfully)
                return waiter.Completion.Task.Result;
        }

        try
        {
            return await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                if (waiter.Completion.Task.IsCompletedSuccessfully)
                    return waiter.Completion.Task.Result;
                _waiters.Remove(waiter);
            }

            throw;
        }
    }

    /// <summary>
    ///     Runs <paramref name="work"/> once for <paramref name="key"/> and shares its answer with every waiter
    ///     whose key matches while it runs. One waiter giving up does not cancel the work the others wait for:
    ///     the shared work is cancelled only when its last waiter leaves before it finishes.
    /// </summary>
    /// <param name="key">The content key: scope, session, analyzer mode and generation content hash, as the router builds it.</param>
    /// <param name="work">The one computation, which runs with a token cancelled when no waiter waits for it.</param>
    /// <param name="cancellationToken">This waiter's own cancellation; it ends only this wait.</param>
    public async Task<EngineResponse> RunCoalescedAsync(string key, Func<CancellationToken, Task<EngineResponse>> work, CancellationToken cancellationToken)
    {
        Shared shared;
        var created = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A check its last waiter gave up on, or whose work already ended in a cancellation or a failure, is
            // not joined: a waiter that arrives after the giving up starts the work again instead of inheriting
            // the cancellation the work it never waited for is about to answer with.
            if (!_inflight.TryGetValue(key, out var existing) || existing.Abandoned
                || existing.Source.IsCancellationRequested
                || existing.Completion.Task.IsCanceled || existing.Completion.Task.IsFaulted)
            {
                existing = new Shared(key);
                _inflight[key] = existing;
                created = true;
            }

            existing.Waiters++;
            shared = existing;
        }

        if (created)
            shared.Runner = RunSharedAsync(shared, work);
        try
        {
            return await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            lock (_lock)
            {
                shared.Waiters--;
                if (shared.Waiters == 0 && !shared.Completion.Task.IsCompleted)
                {
                    // Retired under the lock, so a waiter that arrives before the cancellation lands starts
                    // the work again (above) instead of joining this check.
                    shared.Abandoned = true;
                    if (_inflight.TryGetValue(key, out var registered) && ReferenceEquals(registered, shared))
                        _inflight.Remove(key);
                    cancel = true;
                }

                if (shared.Completion.Task.IsCompleted && _inflight.TryGetValue(key, out var current) && ReferenceEquals(current, shared))
                    _inflight.Remove(key);
            }

            if (cancel)
                await shared.Source.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The token background work of the newest generation runs with, cancelled when a newer generation is published.</summary>
    public CancellationToken BackgroundToken
    {
        get
        {
            lock (_lock)
                return _background.Token;
        }
    }

    /// <summary>
    ///     Publishes <paramref name="generation"/> for background work: the token of every older generation is
    ///     cancelled, so background work on a stale generation stops and work on the newest runs.
    /// </summary>
    public void PublishGeneration(long generation)
    {
        CancellationTokenSource? older = null;
        lock (_lock)
        {
            if (generation <= _backgroundGeneration)
                return;
            _backgroundGeneration = generation;
            older = _background;
            _background = new CancellationTokenSource();
        }

        older.Cancel();
        older.Dispose();
    }

    /// <summary>Releases one reader, records its duration for its lane's median, and admits the next waiter.</summary>
    internal void Release(SchedulerLease lease)
    {
        lock (_lock)
        {
            _running.Remove(lease);
            RecordLocked(lease.Lane, Math.Max(0, (Stopwatch.GetTimestamp() - lease.DequeuedAt) * 1000 / Stopwatch.Frequency));
            if (!_disposed)
                GrantLocked(lease.Holder);
        }
    }

    public void Dispose()
    {
        List<Waiter> waiting;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            waiting = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiting)
            waiter.Completion.TrySetCanceled();
        _background.Cancel();
        _background.Dispose();
    }

    private async Task RunSharedAsync(Shared shared, Func<CancellationToken, Task<EngineResponse>> work)
    {
        try
        {
            var result = await work(shared.Source.Token).ConfigureAwait(false);
            shared.Completion.TrySetResult(result);
        }
        catch (Exception e)
        {
            shared.Completion.TrySetException(e);
        }
        finally
        {
            lock (_lock)
            {
                if (_inflight.TryGetValue(shared.Key, out var current) && ReferenceEquals(current, shared))
                    _inflight.Remove(shared.Key);
            }
        }
    }

    /// <summary>
    ///     Admits waiters while a reader is free: the first interactive waiter, then the first command waiter, then
    ///     the first background waiter, each in arrival order. The waiter a release admits names what held a reader
    ///     while it waited: the longest-running reader still running, or, when that release emptied the pool, the
    ///     holder it released, whose reader is the one the waiter waited for.
    /// </summary>
    /// <param name="releasedHolder">What the release that admits the next waiter held, or null when a new arrival admits it.</param>
    private void GrantLocked(string? releasedHolder = null)
    {
        while (_running.Count < ReaderCount && _waiters.Count > 0)
        {
            var next = _waiters.Find(w => w.Lane == SchedulerLane.Interactive)
                ?? _waiters.Find(w => w.Lane == SchedulerLane.Command)
                ?? _waiters[0];
            _waiters.Remove(next);
            var now = Stopwatch.GetTimestamp();
            var gateHolder = _running.Count == 0
                ? releasedHolder
                : _running.MaxBy(r => now - r.DequeuedAt)!.Holder;
            releasedHolder = null;
            var lease = new SchedulerLease(this, next.Lane, next.Holder, TimeSpan.FromTicks((now - next.Since) * TimeSpan.TicksPerSecond / Stopwatch.Frequency), gateHolder, now);
            _running.Add(lease);
            next.Completion.TrySetResult(lease);
        }
    }

    private (int Position, long EstimatedWaitMs) EstimateLocked(SchedulerLane lane)
    {
        if (_running.Count < ReaderCount)
            return (0, 0);
        // Only the waiters that will start before this one count: a higher lane, or the same lane queued earlier.
        var ahead = _waiters.Count(w => Priority(w.Lane) < Priority(lane) || w.Lane == lane);
        var position = ahead + 1;
        var median = MedianLocked(lane);
        var rounds = (position + ReaderCount - 1) / ReaderCount;
        return (position, rounds * median);
    }

    private long MedianLocked(SchedulerLane lane)
    {
        var values = _durations[lane];
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private void RecordLocked(SchedulerLane lane, long ms)
    {
        var values = _durations[lane];
        values.Add(ms);
        if (values.Count > MedianWindow)
            values.RemoveRange(0, values.Count - MedianWindow);
    }

    private static int Priority(SchedulerLane lane) => lane switch
    {
        SchedulerLane.Interactive => 0,
        SchedulerLane.Command => 1,
        _ => 2,
    };

    /// <summary>One request waiting for a reader: its lane, what it will hold the reader as, and when it arrived.</summary>
    private sealed class Waiter(SchedulerLane lane, string holder, long since)
    {
        public SchedulerLane Lane { get; } = lane;

        public string Holder { get; } = holder;

        public long Since { get; } = since;

        public TaskCompletionSource<SchedulerLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One check in flight, shared by the waiters of its key: its answer, its cancellation and how many waiters hold it.</summary>
    private sealed class Shared(string key)
    {
        public string Key { get; } = key;

        public int Waiters;

        /// <summary>True once the last waiter gave up before the answer, so no later waiter joins this check.</summary>
        public bool Abandoned;

        public CancellationTokenSource Source { get; } = new();

        public TaskCompletionSource<EngineResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Runner;
    }
}
