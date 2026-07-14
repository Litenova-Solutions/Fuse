using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Eager warm-on-start indexing (R38): the daemon or serve host kicks off a background syntax-first index the
///     moment a repo is served, before any tool call, so the first read hits a warm (or bounded-building) index
///     rather than paying the full cold cost. Default-on; opt out with <c>FUSE_EAGER_INDEX=0</c>. The build runs
///     through the shared <see cref="ColdStartCoordinator" /> so an eager start and a later cold read share one
///     build (never two), and it degrades gracefully: a failure is swallowed so serving is never blocked.
/// </summary>
public static class EagerIndex
{
    /// <summary>The environment variable that opts out of eager warm-on-start indexing.</summary>
    public const string EnvVar = "FUSE_EAGER_INDEX";

    /// <summary>Whether eager warm-on-start indexing is enabled (default on; <c>0</c>/<c>false</c>/<c>no</c>/<c>off</c> opts out).</summary>
    /// <returns><see langword="true" /> unless explicitly opted out.</returns>
    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        return value is null
               || !(value.Equals("0", StringComparison.Ordinal)
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Starts the background syntax-first index for a served root, if eager indexing is enabled and the store
    ///     is cold. Returns immediately; the returned task lets callers (tests) await completion. Returns
    ///     <see langword="null" /> when eager indexing is disabled.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="root">The workspace root to warm.</param>
    /// <returns>The build task, or <see langword="null" /> when disabled.</returns>
    public static Task? Start(SemanticIndexer indexer, string root)
    {
        if (!IsEnabled())
            return null;

        var normalizedRoot = Path.GetFullPath(root);
        return ColdStartCoordinator.Default.StartBuild(normalizedRoot, ct => WarmSafelyAsync(indexer, normalizedRoot, ct));
    }

    /// <summary>
    ///     Warms the root's index now, awaitably, ignoring the <c>FUSE_EAGER_INDEX</c> opt-out (explicit
    ///     <c>fuse warm</c>). Only a cold store is built; a warm store is a no-op.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="root">The workspace root to warm.</param>
    /// <param name="cancellationToken">A token to cancel the warm.</param>
    /// <returns>A task that completes when the warm build finishes.</returns>
    public static Task WarmAsync(SemanticIndexer indexer, string root, CancellationToken cancellationToken) =>
        ColdStartCoordinator.Default.StartBuild(Path.GetFullPath(root), ct => WarmSafelyAsync(indexer, Path.GetFullPath(root), cancellationToken));

    private static async Task WarmSafelyAsync(SemanticIndexer indexer, string normalizedRoot, CancellationToken cancellationToken)
    {
        try
        {
            await IndexCoordinator.Default.OpenForWriteAsync(
                normalizedRoot,
                async (store, wct) =>
                {
                    // Only warm a cold store; a warm store is left to the watcher/reconcile path.
                    var state = await store.GetStateAsync(wct);
                    if (state.FileCount == 0)
                    {
                        await indexer.IndexSyntaxFirstAsync(normalizedRoot, store, wct);
                        FuseTools.ScheduleSemanticUpgrade(indexer, normalizedRoot);
                    }

                    return 0;
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is IndexBusyException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Degrade gracefully: eager warm-up is best-effort. If the store is contended or unavailable, the
            // first read's bounded cold-start path (R27) will build it; serving is never blocked by this.
        }
    }
}
