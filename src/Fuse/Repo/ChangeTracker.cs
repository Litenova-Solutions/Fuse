using System.Collections.Concurrent;
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

    /// <summary>How many changed non-sources <see cref="NonSourceSample"/> keeps: the first by sorted relative path.</summary>
    private const int MaxNonSourceSample = 6;

    private readonly RepoRoot _root;
    private readonly HeadResolver _head;
    private readonly WatchedPaths _watched;
    private readonly GitBlobReader _blobs;
    private readonly ConcurrentDictionary<RepoPath, byte> _changed = new();
    private readonly ConcurrentDictionary<RepoPath, byte> _nonSources = new();

    /// <summary>
    ///     What a sync took from the watcher and did not fold in because its reseed failed, or null. The next sync takes it
    ///     first, so it sees the same triggers and paths and seeds again.
    /// </summary>
    private WatchedChanges? _unfolded;
    private HashSet<RepoPath>? _unfoldedNonSources;

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

    /// <summary>The C# and Razor files that differ from HEAD, including deleted ones, as a snapshot a reader can enumerate while a sync folds.</summary>
    public IReadOnlyCollection<RepoPath> Changed => [.. _changed.Keys.OrderBy(p => p.Relative, StringComparer.Ordinal)];

    /// <summary>How many changed files are not sources; the check names them instead of reading them.</summary>
    public int NonSourceChanged { get; private set; }

    /// <summary>The first changed non-sources by sorted relative path.</summary>
    public IReadOnlyList<RepoPath> NonSourceSample { get; private set; } = [];

    /// <summary>The files that differ from HEAD and are not sources, including deleted ones.</summary>
    public IReadOnlyCollection<RepoPath> NonSources => [.. _nonSources.Keys.OrderBy(p => p.Relative, StringComparer.Ordinal)];

    /// <summary>Reads git state and starts watching. Call once before <see cref="SyncAsync"/>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var head = await _head.ResolveAsync(Head, cancellationToken).ConfigureAwait(false);
        var (seeded, nonSources) = await SeedAsync(cancellationToken).ConfigureAwait(false);
        Head = head;
        foreach (var seededPath in seeded) _changed[seededPath] = 0;
        SetNonSources(nonSources);
        _watched.Start();
    }

    /// <summary>Folds every change seen since the last call into <see cref="Changed"/> and says how the workspace follows it.</summary>
    /// <param name="knownPaths">Files a hook reports as written, checked even if their watcher event has not arrived.</param>
    /// <param name="cancellationToken">Cancels a reseed from git.</param>
    /// <param name="confirmWithDisk">True to confirm the changed set against git when the watcher reported nothing.</param>
    /// <exception cref="FuseException">
    ///     git cannot resolve HEAD, list the changes or read a file at HEAD. When listing the changes for a reseed fails,
    ///     <see cref="Head"/> and <see cref="Changed"/> stay as they were and the next call seeds again.
    /// </exception>
    public async Task<SyncResult> SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken, bool confirmWithDisk = false)
    {
        var head = await _head.ResolveAsync(Head, cancellationToken).ConfigureAwait(false);
        var headMoved = !string.Equals(head, Head, StringComparison.Ordinal);
        var watched = Drain();
        var watchedNonSources = DrainNonSources();
        var paths = new HashSet<RepoPath>(watched.Sources);
        // A hook names what the agent wrote, which can be a file the build generated or one outside the repository. The
        // watcher ignores the first, and git has no HEAD for the second, so neither is patched.
        foreach (var path in knownPaths)
        {
            if (_root.Contains(path.Absolute) && !_watched.IsIgnored(path))
                if (PathRules.IsSource(path.Absolute))
                    paths.Add(path);
                else if (!PathRules.IsProjectFile(path.Absolute))
                    watchedNonSources.Add(path);
        }

        // Git-ignored files the watcher reports are not changes: one batched check-ignore call per sync names them.
        if (watchedNonSources.Count > 0)
        {
            var ignored = await GitStatus.IgnoredPathsAsync(_root, watchedNonSources, cancellationToken).ConfigureAwait(false);
            watchedNonSources.ExceptWith(ignored);
            foreach (var ignoredPath in ignored) _nonSources.TryRemove(ignoredPath, out _);
        }

        // A check of every change asks for the changed set to be confirmed against disk: a write the watcher has not
        // reported yet, as a shell command's write on macOS can be late, is in no set. git lists what differs from
        // HEAD, and every source whose membership differs from what the tracker holds joins the reported paths, which
        // the sync below verifies against disk and HEAD like any other. Other syncs never pay for git.
        if (confirmWithDisk && !headMoved && watched.WatcherErrors.Count == 0)
        {
            HashSet<RepoPath> seeded;
            try
            {
                (seeded, _) = await SeedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing is folded in, so the next sync takes what this one drained and confirms again.
                _unfolded = watched with { Sources = new HashSet<RepoPath>(paths) };
                _unfoldedNonSources = watchedNonSources;
                throw;
            }

            paths.UnionWith(seeded.Where(p => !_changed.ContainsKey(p)));
            paths.UnionWith(_changed.Keys.Where(p => !seeded.Contains(p)));
        }

        // After HEAD moves or the watcher loses events, the reported paths are not the whole change, and past the limit
        // comparing them one at a time costs more than asking git again. Every path that may differ from what the
        // workspace holds goes back: changed before the reseed, changed after it, or reported.
        if (headMoved || watched.WatcherErrors.Count > 0 || paths.Count > MaxPatchedPaths)
        {
            var reported = paths.Count;
            HashSet<RepoPath> seeded;
            List<RepoPath> nonSources;
            try
            {
                (seeded, nonSources) = await SeedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing is folded in, so HEAD and the set stay as they were, and the next sync takes what this one
                // drained and reseeds again.
                _unfolded = watched with { Sources = new HashSet<RepoPath>(paths) };
                _unfoldedNonSources = watchedNonSources;
                throw;
            }

            paths.UnionWith(_changed.Keys);
            Head = head;
            _changed.Clear();
            foreach (var seededPath in seeded) _changed[seededPath] = 0;
            SetNonSources(nonSources);
            paths.UnionWith(_changed.Keys);
            if (headMoved)
                return new SyncResult.Reevaluate(paths, head is null ? "HEAD has no commit" : $"HEAD moved to {head[..Math.Min(12, head.Length)]}");
            if (watched.WatcherErrors.Count > 0)
                return new SyncResult.Reevaluate(paths, "watcher error: " + watched.WatcherErrors[^1]);
            if (watched.ProjectFiles.Count > 0)
                return new SyncResult.Reevaluate(paths, watched.ProjectFiles[^1].Absolute);
            return new SyncResult.Reload(paths, $"{reported} changed files");
        }

        foreach (var directory in watched.VanishedDirectories)
        {
            watchedNonSources.UnionWith(_nonSources.Keys.Where(c => c.IsUnder(directory)));
            paths.UnionWith(_changed.Keys.Where(c => c.IsUnder(directory)));
        }

        foreach (var path in paths)
            Refresh(path);
        foreach (var nonSource in watchedNonSources)
            RefreshNonSource(nonSource);
        NoteNonSources();
        return watched.ProjectFiles.Count > 0
            ? new SyncResult.Reevaluate(paths, watched.ProjectFiles[^1].Absolute)
            : new SyncResult.Patch(paths, watched.VanishedDirectories);
    }

    /// <summary>
    ///     Reads a file as it is at HEAD, or null when it does not exist there. A file outside the repository, such as a
    ///     source a project links from a sibling folder, has no HEAD, and asking git for one makes it exit.
    /// </summary>
    public byte[]? ReadHead(RepoPath path) => Head is null || !_root.Contains(path.Absolute) ? null : _blobs.Read(Head, path.Relative);

    /// <summary>
    /// Counts the non-sources <see cref="SeedAsync"/> read and keeps the first by sorted relative path.
    /// </summary>
    private void SetNonSources(List<RepoPath> nonSources)
    {
        _nonSources.Clear();
        foreach (var nonSourcePath in nonSources) _nonSources[nonSourcePath] = 0;
        NoteNonSources();
    }

    /// <summary>
    ///     The sources git reports as changed, with the non-sources beside them. It changes no state, so a failure
    ///     leaves the tracker as it was.
    /// </summary>
    /// <exception cref="FuseException">git status fails.</exception>
    private async Task<(HashSet<RepoPath> Sources, List<RepoPath> NonSources)> SeedAsync(CancellationToken cancellationToken)
    {
        var seeded = new HashSet<RepoPath>();
        var nonSources = new List<RepoPath>();
        foreach (var path in await GitStatus.ChangedPathsAsync(_root, cancellationToken).ConfigureAwait(false))
        {
            if (_watched.IsIgnored(path))
                continue;
            if (PathRules.IsSource(path.Absolute))
                seeded.Add(path);
            else
                nonSources.Add(path);
        }

        return (seeded, nonSources);
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

    /// <summary>What the watcher recorded for non-sources since the last sync, after what a failed reseed took and did not fold in.</summary>
    private HashSet<RepoPath> DrainNonSources()
    {
        var drained = _watched.DrainNonSources();
        if (_unfoldedNonSources is not { } unfolded)
            return drained;
        _unfoldedNonSources = null;
        unfolded.UnionWith(drained);
        return unfolded;
    }

    /// <summary>Counts the non-sources that differ from HEAD and keeps the first by sorted relative path.</summary>
    private void NoteNonSources()
    {
        NonSourceChanged = _nonSources.Count;
        NonSourceSample = [.. _nonSources.Keys.OrderBy(p => p.Relative, StringComparer.Ordinal).Take(MaxNonSourceSample)];
    }

    private void RefreshNonSource(RepoPath path)
    {
        if (_watched.IsIgnored(path))
            return;
        // The watcher reports a change on a directory when its listing changes; the directory is not a changed file.
        if (Directory.Exists(path.Absolute))
        {
            _nonSources.TryRemove(path, out _);
            return;
        }

        if (DiffersFromHead(path))
            _nonSources[path] = 0;
        else
            _nonSources.TryRemove(path, out _);
    }

    private void Refresh(RepoPath path)
    {
        if (_watched.IsIgnored(path))
            return;
        if (DiffersFromHead(path))
            _changed[path] = 0;
        else
            _changed.TryRemove(path, out _);
    }

    private bool DiffersFromHead(RepoPath path)
    {
        var atHead = ReadHead(path);
        byte[]? onDisk = null;
        try
        {
            if (File.Exists(path.Absolute))
                onDisk = SharedRead.AllBytes(path.Absolute);
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

    /// <summary>
    ///     Reports <paramref name="paths"/> as written, as the watcher would, so the next sync verifies them whichever
    ///     request runs it. A hook that credits a session with a file calls this.
    /// </summary>
    public void ReportWritten(IEnumerable<RepoPath> paths)
    {
        foreach (var path in paths)
            _watched.Record(path.Absolute, appearedOrVanished: false);
    }

    public void Dispose()
    {
        _watched.Dispose();
        _blobs.Dispose();
    }
}
