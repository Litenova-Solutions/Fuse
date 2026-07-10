namespace Fuse.Cli.Rpc;

/// <summary>
///     Ensures a shared resident daemon is running for a repository root (G5), spawning one on demand when none
///     serves it. The probe and spawn are injected so the control flow is testable without real processes; the
///     production wiring passes <see cref="FuseHostClient.IsServingAsync" /> as the probe and a
///     <c>fuse host</c> process launch as the spawn. Single-instance safety is enforced by the daemon itself (it
///     acquires <see cref="DaemonLock" /> at startup and exits if another owns it), so a spawn race started here
///     resolves to exactly one daemon regardless of how many callers spawn concurrently.
/// </summary>
public sealed class DaemonSupervisor
{
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Action _spawn;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DaemonSupervisor" /> class.
    /// </summary>
    /// <param name="probe">Returns whether a compatible daemon currently serves the root.</param>
    /// <param name="spawn">Starts a daemon for the root (best-effort; single-instance is enforced by the daemon).</param>
    /// <param name="pollInterval">How often to re-probe while waiting for a spawned daemon to come up.</param>
    public DaemonSupervisor(Func<CancellationToken, Task<bool>> probe, Action spawn, TimeSpan? pollInterval = null)
    {
        _probe = probe;
        _spawn = spawn;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>The outcome of ensuring a daemon is running.</summary>
    public enum Outcome
    {
        /// <summary>A daemon was already serving the root; nothing was spawned.</summary>
        AlreadyRunning,

        /// <summary>No daemon served the root; one was spawned and came up within the timeout.</summary>
        Started,

        /// <summary>No daemon served the root; one was spawned but did not come up within the timeout.</summary>
        FailedToStart,
    }

    /// <summary>
    ///     Ensures a daemon is running for the root: returns immediately when one already serves it, otherwise
    ///     spawns one and polls until it answers or the timeout elapses.
    /// </summary>
    /// <param name="startTimeout">How long to wait for a spawned daemon to start answering.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The outcome.</returns>
    public async Task<Outcome> EnsureRunningAsync(TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        if (await _probe(cancellationToken))
            return Outcome.AlreadyRunning;

        _spawn();

        // Poll until the spawned daemon answers the handshake or the timeout elapses. A concurrent caller that
        // also spawned is harmless: the daemons race the lock and one wins, and either satisfies the probe.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(startTimeout);
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                if (await _probe(cancellationToken))
                    return Outcome.Started;
                await Task.Delay(_pollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The start timeout elapsed; fall through to a final probe below.
        }

        return await _probe(cancellationToken) ? Outcome.Started : Outcome.FailedToStart;
    }
}
