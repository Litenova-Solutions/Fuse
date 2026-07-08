using Fuse.Indexing;
using Fuse.Semantics;
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

    /// <summary>
    ///     Projects the resident state of the projects touched by a set of changed files into the store (S1 step
    ///     4), so a symbol or edge the edit introduced or removed is queryable through the store-backed read tools
    ///     without a full re-index. Maps each changed C# file to the held compilation that contains it, then
    ///     re-projects each affected project via <see cref="SemanticIndexer.ProjectFromCompilationsAsync" />.
    /// </summary>
    /// <remarks>
    ///     This is a store-write: per the single-writer invariant only the serve watcher for this root calls it,
    ///     and the resident read path skips the N6 reconcile so the two do not both write the store. It reads file
    ///     content from disk (chunk extraction), so the edit must already be applied to the resident state and to
    ///     disk.
    /// </remarks>
    /// <param name="indexer">The semantic indexer providing the projection.</param>
    /// <param name="store">The index store to project into.</param>
    /// <param name="changedAbsolutePaths">The absolute paths of the changed files.</param>
    /// <param name="cancellationToken">A token to cancel the projection.</param>
    /// <returns>The number of projects re-projected (zero when no changed file maps to a held project).</returns>
    public async Task<int> ProjectChangedAsync(
        SemanticIndexer indexer, IWorkspaceIndexStore store, IReadOnlyList<string> changedAbsolutePaths, CancellationToken cancellationToken)
    {
        List<ResidentProject> affected;
        lock (_gate)
        {
            var byPath = new Dictionary<string, ResidentProject>(StringComparer.OrdinalIgnoreCase);
            foreach (var changed in changedAbsolutePaths)
            {
                if (!changed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                var normalizedChanged = System.IO.Path.GetFullPath(changed).Replace('\\', '/');
                foreach (var project in _workspace.Projects)
                {
                    if (project.Compilation.SyntaxTrees.Any(t =>
                        t.FilePath.Replace('\\', '/').Equals(normalizedChanged, StringComparison.OrdinalIgnoreCase)))
                    {
                        byPath[project.ProjectFilePath] = project;
                        break;
                    }
                }
            }

            affected = byPath.Values.ToList();
        }

        if (affected.Count == 0)
            return 0;

        var compilations = affected
            .Select(p => (p.ProjectFilePath, p.Compilation))
            .ToList();
        var files = affected
            .SelectMany(p => p.Compilation.SyntaxTrees)
            .Select(t => t.FilePath)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ToFileRecord)
            .ToList();

        await indexer.ProjectFromCompilationsAsync(_root, store, compilations, files, cancellationToken);
        return affected.Count;
    }

    // Builds a minimal file record for a resident source file: the store needs the normalized (root-relative,
    // forward-slash) path to link rows and to read the file's content for chunk extraction.
    private IndexedFileRecord ToFileRecord(string absolutePath)
    {
        var normalized = System.IO.Path.GetRelativePath(_root, absolutePath).Replace('\\', '/');
        var info = new FileInfo(absolutePath);
        return new IndexedFileRecord(
            Path: absolutePath,
            NormalizedPath: normalized,
            Extension: ".cs",
            SizeBytes: info.Exists ? info.Length : 0,
            MtimeUtcTicks: info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            ContentHash: string.Empty);
    }

    private bool Matches(string root) =>
        string.Equals(System.IO.Path.GetFullPath(root), _root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Disposes the held resident workspace.</summary>
    public void Dispose() => _workspace.Dispose();
}
