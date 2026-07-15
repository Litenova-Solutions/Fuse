using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Opens the workspace index for MCP read tools and explicit index actions. The local implementation uses
///     <see cref="IndexCoordinator" /> in-process; the remote implementation delegates writes to a shared
///     <c>fuse host</c> daemon (R19) and opens the store read-only locally for queries.
/// </summary>
public interface IIndexAccessProvider
{
    /// <summary>
    ///     Opens the store for a read tool: cold build, reconcile, and background upgrade on the owning process
    ///     (or the daemon when delegated), then returns a readable store handle.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>A readable store ready for queries.</returns>
    Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken);

    /// <summary>
    ///     Runs an explicit index build or refresh under the coordinator lock (or the daemon RPC when delegated).
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="cancellationToken">A token to cancel the index pass.</param>
    /// <returns>The index pass summary.</returns>
    Task<SemanticIndexResult> IndexAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken);
}
