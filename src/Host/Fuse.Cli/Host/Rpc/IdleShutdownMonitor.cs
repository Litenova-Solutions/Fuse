namespace Fuse.Cli.Rpc;

/// <summary>
///     Shuts a shared daemon down after it has had no connected clients for an idle window (G5), so a daemon
///     spawned on demand does not linger forever holding a resident workspace. The connection count and the
///     shutdown action are injected so the timing logic is testable without a running host; the production wiring
///     passes the host notifier's live connection count and the host's stop signal. A window of zero disables the
///     monitor, preserving the prior always-on behavior for a manually run host.
/// </summary>
public sealed class IdleShutdownMonitor
{
    private readonly Func<int> _connectionCount;
    private readonly Action _shutdown;
    private readonly TimeSpan _idleWindow;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    ///     Initializes a new instance of the <see cref="IdleShutdownMonitor" /> class.
    /// </summary>
    /// <param name="connectionCount">Returns the current number of connected clients.</param>
    /// <param name="shutdown">Invoked once when the daemon has been idle for the whole window.</param>
    /// <param name="idleWindow">How long with zero connections before shutting down; zero or negative disables the monitor.</param>
    /// <param name="pollInterval">How often to sample the connection count.</param>
    public IdleShutdownMonitor(Func<int> connectionCount, Action shutdown, TimeSpan idleWindow, TimeSpan? pollInterval = null)
    {
        _connectionCount = connectionCount;
        _shutdown = shutdown;
        _idleWindow = idleWindow;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Whether this monitor is active (a positive idle window was configured).</summary>
    public bool IsEnabled => _idleWindow > TimeSpan.Zero;

    /// <summary>
    ///     Runs the idle watch until the daemon shuts down or the token is cancelled. Returns immediately when the
    ///     monitor is disabled. The idle clock resets whenever a connection is present, so a daemon shuts down only
    ///     after a full window of no clients; a startup grace equal to the window avoids shutting down before the
    ///     first client connects.
    /// </summary>
    /// <param name="cancellationToken">A token to stop the watch (host shutdown).</param>
    /// <returns>A task that completes when the monitor stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return;

        var idleFor = TimeSpan.Zero;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_pollInterval, cancellationToken);
                if (_connectionCount() > 0)
                {
                    idleFor = TimeSpan.Zero; // A client is connected; reset the idle clock.
                    continue;
                }

                idleFor += _pollInterval;
                if (idleFor >= _idleWindow)
                {
                    _shutdown();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down for another reason; nothing to do.
        }
    }
}
