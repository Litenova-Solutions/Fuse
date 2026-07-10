namespace Fuse.Cli.Rpc;

/// <summary>
///     The race-safe single-instance lock for the shared resident daemon (G5). Exactly one process per repository
///     root may own the daemon; a concurrent spawn race resolves to one winner, and the losers connect to the
///     winner instead. Backed by a named mutex keyed by the root (<see cref="HostEndpoint.DaemonMutexName" />),
///     so the arbitration is atomic across processes without a lock file to leak or a port to collide.
/// </summary>
/// <remarks>
///     A named mutex is owned by the acquiring thread; the owning process must keep the <see cref="DaemonLock" />
///     (and so the mutex) alive for the daemon's lifetime and dispose it on shutdown. If a previous owner crashed
///     without releasing, the next acquirer observes an <see cref="AbandonedMutexException" /> and takes ownership,
///     so a crashed daemon does not wedge the root forever.
/// </remarks>
public sealed class DaemonLock : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _released;

    private DaemonLock(Mutex? mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    /// <summary>Whether this process won the lock and is therefore the daemon for the root.</summary>
    public bool IsOwner { get; }

    /// <summary>
    ///     Attempts to acquire the single-instance daemon lock for a repository root without blocking.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root the daemon would serve.</param>
    /// <returns>
    ///     A lock whose <see cref="IsOwner" /> is <c>true</c> when this call won and should start the daemon, or
    ///     <c>false</c> when another process already owns it and this caller should connect as a client instead.
    /// </returns>
    public static DaemonLock TryAcquire(string repositoryRoot)
    {
        var mutex = new Mutex(initiallyOwned: false, HostEndpoint.DaemonMutexName(repositoryRoot));
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous daemon died without releasing; WaitOne surfaced the abandonment and we now hold it.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return new DaemonLock(null, isOwner: false);
        }

        return new DaemonLock(mutex, isOwner: true);
    }

    /// <summary>
    ///     Releases the lock (owner only), letting a future process become the daemon for the root. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (!IsOwner || _mutex is null || _released)
            return;
        _released = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held by this thread (e.g. released on a different thread); the dispose below still frees it.
        }

        _mutex.Dispose();
    }
}
