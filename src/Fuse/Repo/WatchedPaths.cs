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
    private readonly RepoPath _gitDirectory;
    private readonly ConcurrentDictionary<RepoPath, byte> _sources = new();
    private readonly ConcurrentDictionary<RepoPath, byte> _possibleDirectories = new();
    private readonly ConcurrentQueue<RepoPath> _projectFiles = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private FileSystemWatcher? _watcher;

    public WatchedPaths(RepoRoot root)
    {
        _root = root;
        _gitDirectory = root.PathOf(new GitHead(root.Path).GitDirectory);
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
        var entry = _root.PathOf(path);
        if (IsIgnored(entry))
            return;
        if (PathRules.IsSource(path))
            _sources[entry] = 0;
        else if (PathRules.IsProjectFile(path))
            _projectFiles.Enqueue(entry);
        else if (appearedOrVanished)
            _possibleDirectories[entry] = 0; // A folder name can hold a dot, so only the next sync can say what it is.
    }

    /// <summary>Records that the watcher lost events, for example because its buffer overflowed.</summary>
    /// <param name="message">The watcher's error, which the engine log quotes.</param>
    public void RecordError(string message) => _errors.Enqueue(message);

    /// <summary>Reports <paramref name="path"/> again at the next sync, because it could not be read at this one.</summary>
    public void MarkDirty(RepoPath path) => _sources[path] = 0;

    /// <summary>Takes everything recorded since the previous call.</summary>
    /// <remarks>
    ///     A path that appeared or vanished is resolved here: an existing directory contributes its sources and project
    ///     files, which arrive without events of their own when the directory is moved or renamed, and a path that is
    ///     neither a file nor a directory any more is reported as vanished, so the workspace can drop what it holds under
    ///     it. An existing file that is neither a source nor a project file contributes nothing.
    /// </remarks>
    public WatchedChanges Drain()
    {
        var errors = Take(_errors);
        var projectFiles = Take(_projectFiles);
        var sources = new HashSet<RepoPath>();
        foreach (var key in _sources.Keys)
        {
            if (_sources.TryRemove(key, out _))
                sources.Add(key);
        }

        var vanished = new List<RepoPath>();
        foreach (var key in _possibleDirectories.Keys)
        {
            if (!_possibleDirectories.TryRemove(key, out _))
                continue;
            if (Directory.Exists(key.Absolute))
            {
                foreach (var file in FilesUnder(key))
                {
                    if (PathRules.IsSource(file.Absolute))
                        sources.Add(file);
                    else
                        projectFiles.Add(file);
                }
            }
            else if (!File.Exists(key.Absolute))
                vanished.Add(key);
        }

        return new WatchedChanges(sources, vanished, projectFiles, errors);
    }

    /// <summary>
    ///     True for a path the tracker does not follow: git's own directory and what it holds, or a path under a
    ///     <c>bin</c>, <c>obj</c>, <c>.git</c> or <c>node_modules</c> folder of the repository.
    /// </summary>
    public bool IsIgnored(RepoPath path)
    {
        if (path == _gitDirectory || path.IsUnder(_gitDirectory))
            return true;
        var relative = Path.GetRelativePath(_root.Path, path.Absolute);
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

    /// <summary>The sources and project files under <paramref name="directory"/>, at any depth, that the tracker follows.</summary>
    private List<RepoPath> FilesUnder(RepoPath directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory.Absolute, "*", SearchOption.AllDirectories)
                .Where(f => PathRules.IsSource(f) || PathRules.IsProjectFile(f))
                .Select(_root.PathOf)
                .Where(f => !IsIgnored(f))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<T> Take<T>(ConcurrentQueue<T> queue)
    {
        var taken = new List<T>();
        while (queue.TryDequeue(out var item))
            taken.Add(item);
        return taken;
    }

    public void Dispose() => _watcher?.Dispose();
}
