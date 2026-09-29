using System.Collections.Concurrent;
using Fuse.Paths;

namespace Fuse.Repo;

/// <summary>
///     Follows the working tree with a file watcher and keeps what it reports until the next sync takes it. It only
///     records events: whether a reported file differs from HEAD is decided when the sync reads it.
/// </summary>
internal sealed class WatchedPaths : IDisposable
{
    private readonly RepoRoot _root;
    private readonly string _gitDirectory;
    private readonly ConcurrentDictionary<string, byte> _sources = new(PathRules.PathComparer);
    private readonly ConcurrentDictionary<string, byte> _possibleDirectories = new(PathRules.PathComparer);
    private readonly ConcurrentQueue<string> _projectFiles = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private FileSystemWatcher? _watcher;

    public WatchedPaths(RepoRoot root)
    {
        _root = root;
        _gitDirectory = new GitHead(root.Path).GitDirectory;
    }

    /// <summary>Starts the file watcher. A change made before this call is not reported, so the tracker seeds from git first.</summary>
    public void Start()
    {
        _watcher = new FileSystemWatcher(_root.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => Record(e.FullPath, appearedOrVanished: false);
        _watcher.Created += (_, e) => Record(e.FullPath, appearedOrVanished: true);
        _watcher.Deleted += (_, e) => Record(e.FullPath, appearedOrVanished: true);
        _watcher.Renamed += (_, e) =>
        {
            Record(e.OldFullPath, appearedOrVanished: true);
            Record(e.FullPath, appearedOrVanished: true);
        };
        _watcher.Error += (_, e) => RecordError(e.GetException().Message);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Records one file system event, as the watcher reports it.</summary>
    /// <param name="path">The absolute path the event names.</param>
    /// <param name="appearedOrVanished">
    ///     True for a create, delete or rename, whose path may be a directory. A change event on a directory only means its
    ///     listing changed, and the files in it report themselves.
    /// </param>
    public void Record(string path, bool appearedOrVanished)
    {
        if (IsIgnored(path))
            return;
        if (PathRules.IsSource(path))
            _sources[path] = 0;
        else if (PathRules.IsProjectFile(path))
            _projectFiles.Enqueue(path);
        else if (appearedOrVanished && !Path.HasExtension(path))
            _possibleDirectories[path] = 0; // Resolved at the next sync, when the file system says what it is.
    }

    /// <summary>Records that the watcher lost events, for example because its buffer overflowed.</summary>
    /// <param name="message">The watcher's error, which the engine log quotes.</param>
    public void RecordError(string message) => _errors.Enqueue(message);

    /// <summary>Reports <paramref name="path"/> again at the next sync, because it could not be read at this one.</summary>
    public void MarkDirty(string path) => _sources[path] = 0;

    /// <summary>Takes everything recorded since the previous call.</summary>
    /// <remarks>
    ///     A path that appeared or vanished is resolved here: an existing directory contributes its sources, whose files
    ///     arrive without events of their own, and a path that is neither a file nor a directory any more is reported as
    ///     vanished, so the workspace can drop what it holds under it.
    /// </remarks>
    public WatchedChanges Drain()
    {
        var errors = Take(_errors);
        var projectFiles = Take(_projectFiles);
        var sources = new HashSet<string>(PathRules.PathComparer);
        foreach (var key in _sources.Keys)
        {
            if (_sources.TryRemove(key, out _))
                sources.Add(key);
        }

        var vanished = new List<string>();
        foreach (var key in _possibleDirectories.Keys)
        {
            if (!_possibleDirectories.TryRemove(key, out _))
                continue;
            if (Directory.Exists(key))
                sources.UnionWith(SourcesUnder(key));
            else if (!File.Exists(key))
                vanished.Add(key);
        }

        return new WatchedChanges(sources, vanished, projectFiles, errors);
    }

    /// <summary>
    ///     True for a path the tracker does not follow: inside git's own directory, or under a <c>bin</c>, <c>obj</c>,
    ///     <c>.git</c> or <c>node_modules</c> folder of the repository.
    /// </summary>
    public bool IsIgnored(string path)
    {
        if (path.StartsWith(_gitDirectory, StringComparison.OrdinalIgnoreCase))
            return true;
        var relative = Path.GetRelativePath(_root.Path, path);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private List<string> SourcesUnder(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => PathRules.IsSource(f) && !IsIgnored(f))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Take(ConcurrentQueue<string> queue)
    {
        var taken = new List<string>();
        while (queue.TryDequeue(out var item))
            taken.Add(item);
        return taken;
    }

    public void Dispose() => _watcher?.Dispose();
}
