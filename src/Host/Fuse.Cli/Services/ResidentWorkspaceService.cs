using Fuse.Indexing;
using Fuse.Workspace;

namespace Fuse.Cli.Services;

/// <summary>
///     The concrete <see cref="IResidentWorkspaceProvider" /> backing a live resident workspace for one repository
///     root (S1): it answers the availability description and resident-grade overlay checks the read tools consult,
///     and it applies watcher batches to keep the held compilations current. This is the provider the serve/host
///     registers on <c>FuseTools.ResidentWorkspaces</c>; constructing the workspace and subscribing it to the file
///     watcher is the serve wiring that uses this service.
/// </summary>
/// <remarks>
///     The service owns the <see cref="ResidentWorkspace" /> and disposes it. It tracks a revision that increments
///     on each applied batch, reported as the availability stamp so a client can see the resident state advance.
///     Applying a batch reads file content from disk but never writes the tree (the updater's contract).
/// </remarks>
public sealed class ResidentWorkspaceService : IResidentWorkspaceProvider, IDisposable
{
    private readonly string _root;
    private readonly ResidentWorkspace _workspace;
    private readonly ResidentWorkspaceUpdater _updater = new();
    private readonly object _gate = new();
    private int _revision;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ResidentWorkspaceService" /> class.
    /// </summary>
    /// <param name="root">The absolute repository root this service serves.</param>
    /// <param name="workspace">The live resident workspace; the service takes ownership and disposes it.</param>
    public ResidentWorkspaceService(string root, ResidentWorkspace workspace)
    {
        _root = System.IO.Path.GetFullPath(root);
        _workspace = workspace;
    }

    /// <inheritdoc />
    public ResidentStatus? DescribeResident(string root)
    {
        if (!Matches(root))
            return null;
        lock (_gate)
            return new ResidentStatus(_workspace.Projects.Count, $"revision {_revision}");
    }

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        if (!Matches(root))
            return null;
        lock (_gate)
            return _workspace.CheckOverlay(relativeFilePath, newContent, cancellationToken);
    }

    /// <summary>
    ///     Applies a coalesced watcher batch to the held workspace, advancing the revision.
    /// </summary>
    /// <param name="batch">The file changes from the watcher.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>The update result (applied, added, removed, skipped).</returns>
    public ResidentUpdateResult ApplyBatch(IReadOnlyList<WorkspaceFileChange> batch, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var result = _updater.Apply(_workspace, batch, cancellationToken);
            if (result.Applied + result.Added + result.Removed > 0)
                _revision++;
            return result;
        }
    }

    private bool Matches(string root) =>
        string.Equals(System.IO.Path.GetFullPath(root), _root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Disposes the held resident workspace.</summary>
    public void Dispose() => _workspace.Dispose();
}
