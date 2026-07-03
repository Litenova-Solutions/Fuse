using System.Collections.Concurrent;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Owns the lifetime of the background semantic-upgrade jobs the MCP serve host schedules, so a syntax-first
///     cold start can upgrade to the full semantic graph without a fire-and-forget task that outlives the host or
///     swallows its failures (N3, finding 5). Each job runs under a shared cancellation token tied to host
///     shutdown; a failure is logged rather than dropped; and shutdown cancels and drains the in-flight jobs so no
///     task is orphaned or races teardown.
/// </summary>
public sealed class SemanticUpgradeSupervisor : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private readonly TimeSpan _drainTimeout;
    private int _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticUpgradeSupervisor" /> class.
    /// </summary>
    /// <param name="log">An optional sink for failure and drain diagnostics (stderr in the serve host).</param>
    /// <param name="drainTimeout">The bound on how long <see cref="DisposeAsync" /> waits for in-flight jobs.</param>
    public SemanticUpgradeSupervisor(Action<string>? log = null, TimeSpan? drainTimeout = null)
    {
        _log = log;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Whether at least one upgrade job is currently in flight.</summary>
    public bool HasRunning => !_running.IsEmpty;

    /// <summary>
    ///     Schedules a background upgrade for a workspace root, deduped per root and refused after shutdown.
    /// </summary>
    /// <param name="root">The workspace root; a second schedule for the same root while one runs is a no-op.</param>
    /// <param name="work">The upgrade work, passed the supervisor's cancellation token.</param>
    /// <returns><c>true</c> when the job was scheduled; <c>false</c> when one is already running or the supervisor is disposed.</returns>
    public bool Schedule(string root, Func<CancellationToken, Task> work)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        // Reserve the slot before starting the task so two concurrent cold calls do not both upgrade one root.
        if (!_running.TryAdd(root, Task.CompletedTask))
            return false;

        var task = Task.Run(async () =>
        {
            try
            {
                await work(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown; the syntax-tier index remains usable and a later fuse_index can retry.
            }
            catch (Exception ex)
            {
                // A failure is logged, not swallowed: the operator can see why the semantic upgrade did not land.
                _log?.Invoke($"semantic upgrade for '{root}' failed: {ex.Message}");
            }
            finally
            {
                _running.TryRemove(root, out _);
            }
        });
        _running[root] = task;
        return true;
    }

    /// <summary>
    ///     Cancels the shared token and drains the in-flight upgrade jobs, bounded by the drain timeout, so host
    ///     shutdown leaves no orphaned background task. Idempotent.
    /// </summary>
    /// <returns>A task that completes when the jobs have drained or the drain timeout elapsed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _cts.CancelAsync();
        var inflight = _running.Values.ToArray();
        if (inflight.Length > 0)
        {
            try
            {
                await Task.WhenAll(inflight).WaitAsync(_drainTimeout);
            }
            catch (TimeoutException)
            {
                _log?.Invoke($"semantic upgrade drain timed out after {_drainTimeout.TotalSeconds:F0}s; {_running.Count} job(s) still running.");
            }
            catch (Exception)
            {
                // Individual job failures were already logged in Schedule; the drain itself must not throw.
            }
        }

        _cts.Dispose();
    }
}
