using Fuse.Paths;

namespace Fuse.Repo;

/// <summary>
///     Tracks which source files differ from HEAD. It seeds the set from <see cref="GitStatus"/>, then follows the
///     events <see cref="WatchedPaths"/> records, and verifies each reported path by comparing its content with the HEAD
///     blob (<see cref="HeadComparison"/>). A sync it cannot follow file by file seeds the set again.
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
    private readonly HashSet<string> _changed = new(PathRules.PathComparer);

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

    /// <summary>Absolute paths of C# and Razor files that differ from HEAD, including deleted ones.</summary>
    public IReadOnlyCollection<string> Changed => _changed;

    /// <summary>Reads git state and starts watching. Call once before <see cref="SyncAsync"/>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Head = await _head.ResolveAsync(Head, cancellationToken).ConfigureAwait(false);
        await ReseedAsync(cancellationToken).ConfigureAwait(false);
        _watched.Start();
    }

    /// <summary>Folds every change seen since the last call into <see cref="Changed"/> and says how the workspace follows it.</summary>
    /// <param name="knownPaths">Paths a hook reports as written, checked even if their watcher event has not arrived.</param>
    /// <param name="cancellationToken">Cancels a reseed from git.</param>
    public async Task<SyncResult> SyncAsync(IEnumerable<string> knownPaths, CancellationToken cancellationToken)
    {
        var head = await _head.ResolveAsync(Head, cancellationToken).ConfigureAwait(false);
        var headMoved = !string.Equals(head, Head, StringComparison.Ordinal);
        var watched = _watched.Drain();
        var paths = new HashSet<string>(watched.Sources, PathRules.PathComparer);
        foreach (var path in knownPaths)
        {
            if (PathRules.IsSource(path))
                paths.Add(Path.GetFullPath(path));
        }

        // After HEAD moves or the watcher loses events, the reported paths are not the whole change, and past the limit
        // comparing them one at a time costs more than asking git again. Every path that may differ from what the
        // workspace holds goes back: changed before the reseed, changed after it, or reported.
        if (headMoved || watched.WatcherErrors.Count > 0 || paths.Count > MaxPatchedPaths)
        {
            Head = head;
            var reported = paths.Count;
            paths.UnionWith(_changed);
            await ReseedAsync(cancellationToken).ConfigureAwait(false);
            paths.UnionWith(_changed);
            if (headMoved)
                return new SyncResult.Reevaluate(paths, head is null ? "HEAD has no commit" : $"HEAD moved to {head[..Math.Min(12, head.Length)]}");
            if (watched.WatcherErrors.Count > 0)
                return new SyncResult.Reevaluate(paths, "watcher error: " + watched.WatcherErrors[^1]);
            if (watched.ProjectFiles.Count > 0)
                return new SyncResult.Reevaluate(paths, watched.ProjectFiles[^1]);
            return new SyncResult.Reload(paths, $"{reported} changed files");
        }

        foreach (var directory in watched.VanishedDirectories)
        {
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            paths.UnionWith(_changed.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var path in paths)
            Refresh(path);
        return watched.ProjectFiles.Count > 0
            ? new SyncResult.Reevaluate(paths, watched.ProjectFiles[^1])
            : new SyncResult.Patch(paths, watched.VanishedDirectories);
    }

    /// <summary>Reads a file as it is at HEAD, or null when it does not exist there.</summary>
    public byte[]? ReadHead(string absolutePath) => Head is null ? null : _blobs.Read(Head, _root.Relative(absolutePath));

    private async Task ReseedAsync(CancellationToken cancellationToken)
    {
        _changed.Clear();
        foreach (var path in await GitStatus.ChangedPathsAsync(_root, cancellationToken).ConfigureAwait(false))
        {
            if (PathRules.IsSource(path) && !_watched.IsIgnored(path))
                _changed.Add(path);
        }
    }

    private void Refresh(string path)
    {
        if (_watched.IsIgnored(path))
            return;
        if (DiffersFromHead(path))
            _changed.Add(path);
        else
            _changed.Remove(path);
    }

    private bool DiffersFromHead(string path)
    {
        var atHead = ReadHead(path);
        byte[]? onDisk = null;
        try
        {
            if (File.Exists(path))
                onDisk = File.ReadAllBytes(path);
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
