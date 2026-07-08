using Fuse.Cli.Mcp;
using Fuse.Workspace;

namespace Fuse.Cli.Services;

/// <summary>
///     Wires the resident workspace (S1) into a resident host process (<c>mcp serve</c> or <c>fuse host</c>): it
///     registers a <see cref="ResidentWorkspaceRegistry" /> as the read tools' provider, warms the served root in
///     the background so startup is never blocked by the build, and drives incremental updates from a file
///     watcher's coalesced batches. It is opt-in for now (the <c>FUSE_RESIDENT</c> flag), default off, so a host
///     that does not opt in behaves exactly as before; promotion to default-on is the S1 latency gate.
/// </summary>
public static class ResidentWorkspaceHosting
{
    // A change batch above this many files is treated as a bulk change that outran incremental update: the root is
    // evicted to store-backed (whose N6 reconcile handles staleness) rather than served stale.
    private const int StormThreshold = 300;

    /// <summary>Whether the resident workspace is opted in for this process (the <c>FUSE_RESIDENT</c> flag).</summary>
    /// <returns>True when <c>FUSE_RESIDENT</c> is set to a truthy value (1/true/yes/on).</returns>
    public static bool OptIn()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_RESIDENT");
        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Enables the resident workspace for a root over an existing file watcher: registers the registry as the
    ///     provider, warms the root in the background, and subscribes the watcher's batch to the registry.
    /// </summary>
    /// <param name="root">The absolute repository root the host serves.</param>
    /// <param name="watcher">The host's file watcher; its <see cref="DebouncedFileWatcher.BatchChanged" /> drives updates.</param>
    /// <param name="log">A sink for non-fatal diagnostics (stderr), or null.</param>
    /// <param name="cancellationToken">The host's lifetime token.</param>
    /// <returns>
    ///     A disposable that, on host shutdown, disposes the registry (and its held workspaces) and restores the
    ///     default null provider. The caller owns the watcher's lifetime.
    /// </returns>
    public static IDisposable Enable(
        string root, DebouncedFileWatcher watcher, Action<string>? log, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var registry = new ResidentWorkspaceRegistry();
        FuseTools.ResidentWorkspaces = registry;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await registry.WarmAsync(fullRoot, cancellationToken))
                    log?.Invoke($"resident workspace: {fullRoot} did not build; serving store-backed.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"resident workspace warm failed: {ex.Message}");
            }
        }, cancellationToken);

        watcher.BatchChanged += (batch, _) =>
        {
            if (batch.Count > StormThreshold)
                registry.Evict(fullRoot); // A bulk change outran incremental update; fall back to store-backed.
            else
                registry.ApplyBatch(fullRoot, batch, cancellationToken);
            return Task.CompletedTask;
        };

        return new ResidentScope(registry);
    }

    // Restores the default provider and disposes the registry (and its held workspaces) on host shutdown. The
    // watcher is owned by the caller and disposed there.
    private sealed class ResidentScope(ResidentWorkspaceRegistry registry) : IDisposable
    {
        public void Dispose()
        {
            registry.Dispose();
            FuseTools.ResidentWorkspaces = NullResidentWorkspaceProvider.Instance;
        }
    }
}
