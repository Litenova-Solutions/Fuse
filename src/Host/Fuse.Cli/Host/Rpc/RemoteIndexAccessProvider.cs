using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Rpc;

/// <summary>
///     An <see cref="IIndexAccessProvider" /> that delegates index writes to a shared daemon over the pipe (R19),
///     so one daemon-owned <c>IndexCoordinator</c> serves every MCP client for a root. After the daemon prepares
///     the store, this process opens it read-only locally for queries. When no compatible daemon answers, it falls
///     back to <see cref="LocalIndexAccessProvider" /> (R14), never a raw store open.
/// </summary>
public sealed class RemoteIndexAccessProvider : IIndexAccessProvider
{
    private readonly Func<string, TimeSpan, CancellationToken, Task<OpenIndexedResultDto?>> _openIndexed;
    private readonly Func<string, TimeSpan, CancellationToken, Task<IndexResultDto?>> _index;
    private readonly TimeSpan _connectTimeout;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RemoteIndexAccessProvider" /> class.
    /// </summary>
    /// <param name="openIndexed">
    ///     The open-indexed RPC call (root, timeout, token). Injected for tests; production uses
    ///     <see cref="FuseHostClient.TryOpenIndexedAsync" />.
    /// </param>
    /// <param name="index">
    ///     The explicit index RPC call. Injected for tests; production uses <see cref="FuseHostClient.TryIndexAsync" />.
    /// </param>
    /// <param name="connectTimeout">How long to wait for a daemon connection.</param>
    public RemoteIndexAccessProvider(
        Func<string, TimeSpan, CancellationToken, Task<OpenIndexedResultDto?>>? openIndexed = null,
        Func<string, TimeSpan, CancellationToken, Task<IndexResultDto?>>? index = null,
        TimeSpan? connectTimeout = null)
    {
        _openIndexed = openIndexed ?? FuseHostClient.TryOpenIndexedAsync;
        _index = index ?? FuseHostClient.TryIndexAsync;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        var remote = await _openIndexed(root, _connectTimeout, cancellationToken);
        if (remote is null)
            return await LocalIndexAccessProvider.Instance.OpenIndexedAsync(indexer, path, cancellationToken);

        switch (remote.Status)
        {
            case "ready":
                return await OpenReadableStoreAsync(root, cancellationToken);
            case "index_rebuilding":
                throw new IndexRebuildingException(remote.Detail ?? "rebuilding from source");
            case "index_busy":
                throw new IndexBusyException();
            case "not_indexed":
                throw new InvalidOperationException("daemon reported not_indexed after openIndexed");
            default:
                throw new InvalidOperationException($"unexpected daemon openIndexed status '{remote.Status}'.");
        }
    }

    /// <inheritdoc />
    public async Task<SemanticIndexResult> IndexAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        var remote = await _index(root, _connectTimeout, cancellationToken);
        if (remote is null)
            return await LocalIndexAccessProvider.Instance.IndexAsync(indexer, path, cancellationToken);

        return new SemanticIndexResult(
            remote.Mode,
            remote.FileCount,
            ProjectCount: 0,
            remote.SymbolCount,
            ChunkCount: 0,
            remote.RouteCount,
            Diagnostics: []);
    }

    private static async Task<WorkspaceIndexStore> OpenReadableStoreAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        var store = new WorkspaceIndexStore(databasePath);
        var status = await store.OpenForReadAsync(cancellationToken);
        if (status is WorkspaceIndexReadOpenStatus.Ready)
            return store;

        return await IndexCoordinator.Default.OpenForReadOnlyAsync(root, cancellationToken);
    }
}
