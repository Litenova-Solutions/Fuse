using Fuse.Failures;
using Fuse.Paths;

namespace Fuse.Repo;

/// <summary>
///     Tracks which source files differ from HEAD. It seeds the set from <see cref="GitStatus"/>, then follows the
///     events <see cref="WatchedPaths"/> records, and verifies each reported path by comparing its content with the HEAD
///     blob (<see cref="HeadComparison"/>). A sync it cannot follow file by file seeds the set again, and takes the new
///     HEAD and set only once git has listed the changes.
/// </summary>
internal sealed class ChangeTracker : IDisposable
{
    /// <summary>
    ///     The most changed sources a sync patches one at a time. Past it, reloading the projects and reading git status
    ///     again costs less than comparing and applying each file.
    /// </summary>
    private const int MaxPatchedPaths = 300;

    private readonly RepoRoot _root;
    private readonly HeadResolver _head;
    private readonly WatchedPaths _watched;
    private readonly GitBlobReader _blobs;
    private readonly HashSet<RepoPath> _changed = [];

    /// <summary>
    ///     What a sync took from the watcher and did not fold in because its reseed failed, or null. The next sync takes it
    ///     first, so it sees the same triggers and paths and seeds again.
    /// </summary>
    private WatchedChanges? _unfolded;

    public ChangeTracker(RepoRoot root)
        : this(root, new WatchedPaths(root))
    {
    }

    /// <summary>Follows the events <paramref name="watched"/> records, so a test can record them itself instead of waiting for the watcher.</summary>
    public ChangeTracker(RepoRoot root, WatchedPaths watched)
    {
        _root = root;
        _head = new HeadResolver(root);
        _watched = watched;
        _blobs = new GitBlobReader(root.Path);
    }

    /// <summary>The commit the baseline is taken from, or null in a repository without commits.</summary>
    public string? Head { get; private set; }

    /// <summary>The C# and Razor files that differ from HEAD, including deleted ones.</summary>
    public IReadOnlyCollection<RepoPath> Changed => _changed;

    /// <summary>Reads git state and starts watching. Call once before <see cref="SyncAsync"/>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var head = await _head.ResolveAsync(Head, cancellationToken).ConfigureAwait(false);
        var seeded = await SeedAsync(cancellationToken).ConfigureAwait(false);
        Head = head;
        _changed.UnionWith(seeded);
        _watched.Start();
    }

    /// <summary>Folds every change seen since the last call into <see cref="Changed"/> and says how the workspace follows it.</summary>
    /// <param name="knownPaths">Files a hook reports as written, checked even if their watcher event has not arrived.</param>
    /// <param name="cancellationToken">Cancels a reseed from git.</param>
    /// <exception cref="FuseException">
    ///     git cannot resolve HEAD, list the changes or read a file at HEAD. When listing the changes for a reseed fails,
    ///     <see cref="Head"/> and <see cref="Changed"/> stay as they were and the next call seeds again.
    /// </exception>
    public async Task<SyncResult> SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken)
    {
        var head = await _head.ResolveAsync(Head, cancellationToken).ConfigureAwait(false);
        var headMoved = !string.Equals(head, Head, StringComparison.Ordinal);
        var watched = Drain();
        var paths = new HashSet<RepoPath>(watched.Sources);
        foreach (var path in knownPaths)
        {
            if (PathRules.IsSource(path.Absolute))
                paths.Add(path);
        }

        // After HEAD moves or the watcher loses events, the reported paths are not the whole change, and past the limit
        // comparing them one at a time costs more than asking git again. Every path that may differ from what the
        // workspace holds goes back: changed before the reseed, changed after it, or reported.
        if (headMoved || watched.WatcherErrors.Count > 0 || paths.Count > MaxPatchedPaths)
        {
            var reported = paths.Count;
            HashSet<RepoPath> seeded;
            try
            {
                seeded = await SeedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing is folded in, so HEAD and the set stay as they were, and the next sync takes what this one
                // drained and reseeds again.
                _unfolded = watched with { Sources = new HashSet<RepoPath>(paths) };
                throw;
            }

            paths.UnionWith(_changed);
            Head = head;
            _changed.Clear();
            _changed.UnionWith(seeded);
            paths.UnionWith(_changed);
            if (headMoved)
                return new SyncResult.Reevaluate(paths, head is null ? "HEAD has no commit" : $"HEAD moved to {head[..Math.Min(12, head.Length)]}");
            if (watched.WatcherErrors.Count > 0)
                return new SyncResult.Reevaluate(paths, "watcher error: " + watched.WatcherErrors[^1]);
            if (watched.ProjectFiles.Count > 0)
                return new SyncResult.Reevaluate(paths, watched.ProjectFiles[^1].Absolute);
            return new SyncResult.Reload(paths, $"{reported} changed files");
        }

        foreach (var directory in watched.VanishedDirectories)
            paths.UnionWith(_changed.Where(c => c.IsUnder(directory)));

        foreach (var path in paths)
            Refresh(path);
        return watched.ProjectFiles.Count > 0
            ? new SyncResult.Reevaluate(paths, watched.ProjectFiles[^1].Absolute)
            : new SyncResult.Patch(paths, watched.VanishedDirectories);
    }

    /// <summary>Reads a file as it is at HEAD, or null when it does not exist there.</summary>
    public byte[]? ReadHead(RepoPath path) => Head is null ? null : _blobs.Read(Head, path.Relative);

    /// <summary>
    ///     The sources git reports as changed, which replace <see cref="Changed"/> once the caller has them. It changes no
    ///     state, so a failure leaves the tracker as it was.
    /// </summary>
    /// <exception cref="FuseException">git status fails.</exception>
    private async Task<HashSet<RepoPath>> SeedAsync(CancellationToken cancellationToken)
    {
        var seeded = new HashSet<RepoPath>();
        foreach (var path in await GitStatus.ChangedPathsAsync(_root, cancellationToken).ConfigureAwait(false))
        {
            if (PathRules.IsSource(path.Absolute) && !_watched.IsIgnored(path))
                seeded.Add(path);
        }

        return seeded;
    }

    /// <summary>What the watcher recorded since the last sync, after what a failed reseed took and did not fold in.</summary>
    private WatchedChanges Drain()
    {
        var drained = _watched.Drain();
        if (_unfolded is not { } unfolded)
            return drained;
        _unfolded = null;
        return new WatchedChanges(
            new HashSet<RepoPath>([.. unfolded.Sources, .. drained.Sources]),
            [.. unfolded.VanishedDirectories, .. drained.VanishedDirectories],
            [.. unfolded.ProjectFiles, .. drained.ProjectFiles],
            [.. unfolded.WatcherErrors, .. drained.WatcherErrors]);
    }

    private void Refresh(RepoPath path)
    {
        if (_watched.IsIgnored(path))
            return;
        if (DiffersFromHead(path))
            _changed.Add(path);
        else
            _changed.Remove(path);
    }

    private bool DiffersFromHead(RepoPath path)
    {
        var atHead = ReadHead(path);
        byte[]? onDisk = null;
        try
        {
            if (File.Exists(path.Absolute))
                onDisk = File.ReadAllBytes(path.Absolute);
        }
        catch (IOException)
        {
            // Still being written: treat it as changed and look again at the next sync.
            _watched.MarkDirty(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        return HeadComparison.Differs(atHead, onDisk);
    }

    public void Dispose()
    {
        _watched.Dispose();
        _blobs.Dispose();
    }
}
