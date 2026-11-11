using System.Diagnostics;

namespace Fuse.Engine;

/// <summary>
///     The lock the background load and the first evaluation hold while they choose their work: it admits one holder
///     at a time, and tells each holder what held it longest while it waited. Requests take a reader from the
///     scheduler instead, so this lock never serializes a check.</summary>
/// <remarks>
///     Every holder names itself on entry: the background load as <see cref="Preload"/> and the first evaluation
///     as <see cref="Initialization"/>. When a holder exits, the time it held the lock is added, per waiter, to
///     the part of that waiter's wait it overlapped. A priority waiter runs before every normal waiter queued.
///     It still waits for the holder that is already running.
///     Priority waiters keep arrival order among themselves.
///     No request waits on this lock.
/// </remarks>
internal sealed class RequestGate : IDisposable
{
    /// <summary>The name the background load holds the lock under, while it chooses the next project.</summary>
    public const string Preload = "Preload";

    /// <summary>The name the first evaluation and preload hold the lock under.</summary>
    public const string Initialization = "Initialization";

    /// <summary>Guards the waiters and the holder; it is held only to update them, never while the lock is awaited.</summary>
    private readonly Lock _accounting = new();

    private readonly List<Waiter> _waiters = [];
    private string? _holder;
    private long _heldSince;
    private bool _disposed;

    /// <summary>What holds the lock, or null when no holder runs. A status answer reads it to name the holder it did not wait behind.</summary>
    public string? CurrentHolder
    {
        get
        {
            lock (_accounting)
                return _holder;
        }
    }

    /// <summary>Waits for the lock and holds it under <paramref name="holder"/>.</summary>
    /// <param name="holder">What is about to hold the lock, as the next waiter's phase line names it.</param>
    /// <param name="cancellationToken">Stops waiting; the lock is then not held.</param>
    /// <returns>What held the lock longest while this caller waited, or null when nothing held it during the wait.</returns>
    public Task<string?> EnterAsync(string holder, CancellationToken cancellationToken) =>
        EnterAsync(holder, priority: false, cancellationToken);

    /// <summary>
    ///     Waits for the lock ahead of every queued normal waiter and holds it under <paramref name="holder"/>. Status
    ///     and session registrations wait this way, so a short request is not held behind checks that queued first.
    /// </summary>
    /// <param name="holder">What is about to hold the lock, as the next waiter's phase line names it.</param>
    /// <param name="cancellationToken">Stops waiting; the lock is then not held.</param>
    /// <returns>What held the lock longest while this caller waited, or null when nothing held it during the wait.</returns>
    public Task<string?> EnterPriorityAsync(string holder, CancellationToken cancellationToken) =>
        EnterAsync(holder, priority: true, cancellationToken);

    private async Task<string?> EnterAsync(string holder, bool priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter(holder, priority, Stopwatch.GetTimestamp());
        string? granted = null;
        var isGranted = false;
        lock (_accounting)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _waiters.Add(waiter);
            GrantNextLocked();
            isGranted = waiter.Completion.Task.IsCompletedSuccessfully;
            if (isGranted)
                granted = waiter.Completion.Task.Result;
        }

        if (isGranted)
            return granted;

        try
        {
            return await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_accounting)
            {
                // A grant that raced with the cancellation holds the lock; the caller runs and exits it as a holder.
                if (waiter.Completion.Task.IsCompletedSuccessfully)
                    return waiter.Completion.Task.Result;
                _waiters.Remove(waiter);
            }

            throw;
        }
    }

    /// <summary>Releases the lock, crediting the time it was held to every caller still waiting, and admits the next waiter.</summary>
    public void Exit()
    {
        lock (_accounting)
        {
            var now = Stopwatch.GetTimestamp();
            if (_holder is not null)
            {
                foreach (var waiter in _waiters)
                {
                    var overlap = now - Math.Max(_heldSince, waiter.Since);
                    if (overlap > 0)
                        waiter.Held[_holder] = waiter.Held.GetValueOrDefault(_holder) + overlap;
                }
            }

            _holder = null;
            if (!_disposed)
                GrantNextLocked();
        }
    }

    /// <summary>Disposes the lock, once the engine has stopped serving requests and the background load has stopped.</summary>
    public void Dispose()
    {
        lock (_accounting)
        {
            _disposed = true;
            foreach (var waiter in _waiters)
                waiter.Completion.TrySetCanceled();
            _waiters.Clear();
            _holder = null;
        }
    }

    /// <summary>Admits the next waiter while the lock is free: the first priority waiter, or the first waiter when none has priority.</summary>
    private void GrantNextLocked()
    {
        if (_holder is not null || _waiters.Count == 0)
            return;
        var next = _waiters.Find(w => w.Priority) ?? _waiters[0];
        _waiters.Remove(next);
        _holder = next.Holder;
        _heldSince = Stopwatch.GetTimestamp();
        next.Completion.TrySetResult(next.Held.Count == 0 ? null : next.Held.MaxBy(h => h.Value).Key);
    }

    /// <summary>
    ///     One caller waiting for the lock: what it will hold the lock as, whether it has priority, when it started, and
    ///     how long each holder held the lock since. A class, not a record, so two waiters that started on the same tick
    ///     stay two entries.
    /// </summary>
    private sealed class Waiter(string holder, bool priority, long since)
    {
        public string Holder { get; } = holder;

        public bool Priority { get; } = priority;

        public long Since { get; } = since;

        public Dictionary<string, long> Held { get; } = new(StringComparer.Ordinal);

        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
