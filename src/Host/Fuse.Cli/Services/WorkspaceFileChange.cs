namespace Fuse.Cli.Services;

/// <summary>
///     The kind of a workspace file change the watcher observed (S1 step 3): a file was created, its contents
///     changed, or it was deleted. A rename is decomposed into a delete of the old path and a create of the new.
/// </summary>
public enum FileChangeKind
{
    /// <summary>The file was created.</summary>
    Created,

    /// <summary>The file's contents changed.</summary>
    Changed,

    /// <summary>The file was deleted.</summary>
    Deleted,
}

/// <summary>
///     One coalesced workspace file change (S1 step 3): the net effect on a single path over a debounce window.
/// </summary>
/// <param name="Kind">The net change kind for the path.</param>
/// <param name="FullPath">The absolute path that changed.</param>
public sealed record WorkspaceFileChange(FileChangeKind Kind, string FullPath);

/// <summary>
///     Accumulates raw filesystem events over a debounce window and coalesces them to the net change per path,
///     so the resident workspace applies one update per file rather than replaying every raw event (S1 step 3).
///     This is the logic the debounced watcher feeds; it is a plain accumulator with no I/O so it can be tested
///     directly.
/// </summary>
/// <remarks>
///     Coalescing rules per path: a create followed by a delete cancels (a transient file); a delete followed by
///     a create becomes a change (the file was replaced); a create followed by a change stays a create (still a
///     new file); otherwise the latest kind wins. <see cref="Drain" /> returns the net changes and clears the
///     accumulator for the next window. The accumulator is thread-safe because raw events arrive on watcher
///     threads.
/// </remarks>
public sealed class WorkspaceFileChangeSet
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileChangeKind> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of distinct paths currently accumulated (used against the storm threshold).</summary>
    public int Count
    {
        get { lock (_gate) { return _byPath.Count; } }
    }

    /// <summary>
    ///     Records a raw change for a path, coalescing it with any pending change for the same path.
    /// </summary>
    /// <param name="kind">The raw change kind.</param>
    /// <param name="fullPath">The absolute path that changed.</param>
    public void Add(FileChangeKind kind, string fullPath)
    {
        lock (_gate)
        {
            if (!_byPath.TryGetValue(fullPath, out var existing))
            {
                _byPath[fullPath] = kind;
                return;
            }

            switch (existing, kind)
            {
                case (FileChangeKind.Created, FileChangeKind.Deleted):
                    _byPath.Remove(fullPath); // Created then deleted within the window: net nothing.
                    break;
                case (FileChangeKind.Deleted, FileChangeKind.Created):
                    _byPath[fullPath] = FileChangeKind.Changed; // Deleted then recreated: a modification.
                    break;
                case (FileChangeKind.Created, FileChangeKind.Changed):
                    _byPath[fullPath] = FileChangeKind.Created; // Still a new file.
                    break;
                default:
                    _byPath[fullPath] = kind; // Latest wins.
                    break;
            }
        }
    }

    /// <summary>
    ///     Returns the net changes accumulated so far and clears the accumulator.
    /// </summary>
    /// <returns>The coalesced changes, one per path.</returns>
    public IReadOnlyList<WorkspaceFileChange> Drain()
    {
        lock (_gate)
        {
            var changes = _byPath.Select(kv => new WorkspaceFileChange(kv.Value, kv.Key)).ToList();
            _byPath.Clear();
            return changes;
        }
    }
}
