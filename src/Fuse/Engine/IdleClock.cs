namespace Fuse.Engine;

/// <summary>
///     Tracks the requests an engine is serving and how long it has been since the last one, which decide when the engine
///     returns its free memory and when it exits.
/// </summary>
internal sealed class IdleClock
{
    /// <summary>
    ///     An engine with no request for this long exits; the next client starts a fresh one, which loads the projects and
    ///     runs HEAD's analyzers again. Agents often think, read or run commands for more than ten minutes between two
    ///     checks, so the engine waits longer than that.
    /// </summary>
    public static readonly TimeSpan ExitAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     An engine with no request for this long returns the memory its last requests freed. Agents often send several
    ///     requests in a row, so it does not happen after each one.
    /// </summary>
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     An engine with no request for this long leaves early when another engine needs memory: minutes instead
    ///     of <see cref="ExitAfter"/>. It comes back fast, because the stored graph and the summaries are kept, so
    ///     leaving costs an evaluation it does not repeat.
    /// </summary>
    public static readonly TimeSpan ExitAfterShortened = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private int _active;
    private long _lastRequest;
    private int _released;

    public IdleClock(TimeProvider time)
    {
        _time = time;
        _lastRequest = time.GetTimestamp();
    }

    /// <summary>Marks a request as started; the clock is not idle until it ends.</summary>
    public void Begin()
    {
        Interlocked.Increment(ref _active);
        Touch();
    }

    /// <summary>Marks a request as ended, which starts the idle time again.</summary>
    public void End()
    {
        Touch();
        Interlocked.Decrement(ref _active);
    }

    /// <summary>Restarts the idle time, for a connection that sends a line but no request the engine serves.</summary>
    public void Touch()
    {
        Interlocked.Exchange(ref _lastRequest, _time.GetTimestamp());
        Interlocked.Exchange(ref _released, 0);
    }

    /// <summary>True when no request is running and none came for <see cref="ExitAfter"/>.</summary>
    public bool ShouldExit => Volatile.Read(ref _active) == 0 && IdleFor >= ExitAfter;

    /// <summary>True when no request is running and none came for <see cref="ExitAfterShortened"/>, the shortened idle an engine leaves on when another engine needs memory.</summary>
    public bool ShouldExitShortened => Volatile.Read(ref _active) == 0 && IdleFor >= ExitAfterShortened;

    /// <summary>True when the engine leaves now: after <see cref="ExitAfterShortened"/> when <paramref name="machineNeedsMemory"/> holds, and after <see cref="ExitAfter"/> otherwise.</summary>
    public bool ShouldExitWhen(bool machineNeedsMemory) => machineNeedsMemory ? ShouldExitShortened : ShouldExit;

    /// <summary>
    ///     True once per idle period: the first time no request is running and none came for <see cref="ReleaseAfter"/>.
    ///     A request resets it.
    /// </summary>
    public bool TakeRelease() =>
        Volatile.Read(ref _active) == 0 && IdleFor >= ReleaseAfter && Interlocked.Exchange(ref _released, 1) == 0;

    /// <summary>How many requests are running.</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>How long no request has run, which the machine memory budget compares between engines.</summary>
    public TimeSpan IdleDuration => IdleFor;

    private TimeSpan IdleFor => _time.GetElapsedTime(Interlocked.Read(ref _lastRequest));
}
