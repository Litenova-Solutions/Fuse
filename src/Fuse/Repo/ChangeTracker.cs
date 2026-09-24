using System.Collections.Concurrent;
using Fuse.Dotnet;

namespace Fuse.Repo;

/// <summary>What changed on disk since the previous <see cref="ChangeTracker.Sync"/>.</summary>
/// <param name="HeadMoved">HEAD now points at a different commit, so every baseline is stale.</param>
/// <param name="SourcePaths">Absolute paths of C# and Razor files whose content may differ from what the engine last saw.</param>
/// <param name="ProjectFilesChanged">A project, props, targets, editorconfig or global.json file changed, so evaluation is stale.</param>
/// <param name="Storm">Too many files changed at once (a branch switch or a watcher overflow); reloading is cheaper than patching.</param>
/// <param name="VanishedDirectories">Directories that were deleted or renamed away; sources the engine knows under them are gone.</param>
/// <param name="Trigger">A path that caused a reload, for the log.</param>
internal sealed record ChangeBatch(
    bool HeadMoved,
    IReadOnlyCollection<string> SourcePaths,
    bool ProjectFilesChanged,
    bool Storm,
    IReadOnlyCollection<string> VanishedDirectories,
    string? Trigger = null);

/// <summary>
///     Tracks which source files differ from HEAD. It seeds the set from <c>git status</c> once, then follows a
///     file watcher, and verifies each reported path by comparing its content with the HEAD blob.
/// </summary>
internal sealed class ChangeTracker : IDisposable
{
    private const int StormThreshold = 300;

    private static readonly string[] SourceExtensions = [".cs", ".razor", ".cshtml"];
    private static readonly string[] ProjectExtensions = [".csproj", ".props", ".targets", ".editorconfig", ".globalconfig"];

    private readonly RepoRoot _root;
    private readonly GitHead _gitHead;
    private readonly GitBlobReader _blobs;
    private readonly ConcurrentDictionary<string, byte> _dirty = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _structural = new(PathComparer);
    private readonly HashSet<string> _changed = new(PathComparer);
    private FileSystemWatcher? _watcher;
    private volatile bool _overflow;
    private volatile bool _projectFilesDirty;
    private volatile string? _trigger;

    public ChangeTracker(RepoRoot root)
    {
        _root = root;
        _gitHead = new GitHead(root.Path);
        _blobs = new GitBlobReader(root.Path);
    }

    /// <summary>Compares paths the way the file system does.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The commit the baseline is taken from, or null in a repository without commits.</summary>
    public string? Head { get; private set; }

    /// <summary>Absolute paths of C# and Razor files that differ from HEAD, including deleted ones.</summary>
    public IReadOnlyCollection<string> Changed => _changed;

    /// <summary>Reads git state and starts watching. Call once before <see cref="Sync"/>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Head = _gitHead.Read();
        await ReseedAsync(cancellationToken).ConfigureAwait(false);
        _watcher = new FileSystemWatcher(_root.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => OnEvent(e.FullPath, structural: false);
        _watcher.Created += (_, e) => OnEvent(e.FullPath, structural: true);
        _watcher.Deleted += (_, e) => OnEvent(e.FullPath, structural: true);
        _watcher.Renamed += (_, e) =>
        {
            OnEvent(e.OldFullPath, structural: true);
            OnEvent(e.FullPath, structural: true);
        };
        _watcher.Error += (_, e) =>
        {
            _trigger = "watcher error: " + e.GetException().Message;
            _overflow = true;
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Folds every change seen since the last call into <see cref="Changed"/> and reports what moved.</summary>
    /// <param name="knownPaths">Paths a caller knows were just written (a hook payload), checked even if their watcher event has not arrived.</param>
    /// <param name="cancellationToken">Cancels a reseed from git.</param>
    public async Task<ChangeBatch> SyncAsync(IEnumerable<string> knownPaths, CancellationToken cancellationToken)
    {
        var head = _gitHead.Read();
        var headMoved = !string.Equals(head, Head, StringComparison.Ordinal);
        var overflow = _overflow;
        _overflow = false;
        var projectFiles = _projectFilesDirty;
        _projectFilesDirty = false;

        var paths = new HashSet<string>(PathComparer);
        foreach (var key in _dirty.Keys)
        {
            if (_dirty.TryRemove(key, out _))
                paths.Add(key);
        }

        foreach (var path in knownPaths)
        {
            if (IsSource(path))
                paths.Add(Path.GetFullPath(path));
        }

        // Extensionless paths that were created, deleted or renamed may be directories, whose files arrive without
        // their own events. A directory that exists now contributes its sources; one that is gone is reported so the
        // workspace can drop what it knows under it.
        var vanished = new List<string>();
        foreach (var key in _structural.Keys)
        {
            if (!_structural.TryRemove(key, out _))
                continue;
            if (Directory.Exists(key))
                paths.UnionWith(SourcesUnder(key));
            else if (!File.Exists(key))
                vanished.Add(key);
        }

        if (headMoved || overflow || paths.Count > StormThreshold)
        {
            Head = head;
            var before = new HashSet<string>(_changed, PathComparer);
            await ReseedAsync(cancellationToken).ConfigureAwait(false);
            before.UnionWith(_changed);
            before.UnionWith(paths);
            return new ChangeBatch(headMoved, before, projectFiles || headMoved || overflow, Storm: true, vanished, _trigger);
        }

        foreach (var directory in vanished)
        {
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            paths.UnionWith(_changed.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var path in paths)
            Refresh(path);
        return new ChangeBatch(false, paths, projectFiles, Storm: false, vanished, projectFiles ? _trigger : null);
    }

    /// <summary>Reads a file as it is at HEAD, or null when it does not exist there.</summary>
    public byte[]? ReadHead(string absolutePath) => Head is null ? null : _blobs.Read(Head, _root.Relative(absolutePath));

    private async Task ReseedAsync(CancellationToken cancellationToken)
    {
        _changed.Clear();
        var result = await ProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotepath=off", "status", "--porcelain=v1", "--untracked-files=all", "--no-renames", "--ignored=no"],
            _root.Path,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4)
                continue;
            var relative = line[3..].Trim();
            if (relative.Length > 1 && relative[0] == '"' && relative[^1] == '"')
                relative = relative[1..^1];
            var absolute = Path.GetFullPath(Path.Combine(_root.Path, relative));
            if (IsSource(absolute) && !IsIgnoredDirectory(absolute))
                _changed.Add(absolute);
        }
    }

    private List<string> SourcesUnder(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => IsSource(f) && !IsIgnoredDirectory(f))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Refresh(string path)
    {
        if (IsIgnoredDirectory(path))
            return;
        if (DiffersFromHead(path))
            _changed.Add(path);
        else
            _changed.Remove(path);
    }

    private bool DiffersFromHead(string path)
    {
        var headBytes = ReadHead(path);
        byte[]? diskBytes = null;
        try
        {
            if (File.Exists(path))
                diskBytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            // Still being written; treat it as changed so the next sync looks again.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        if (headBytes is null || diskBytes is null)
            return headBytes is not null || diskBytes is not null;
        return !SameIgnoringLineEndings(headBytes, diskBytes);
    }

    private static bool SameIgnoringLineEndings(byte[] a, byte[] b)
    {
        int i = 0, j = 0;
        while (true)
        {
            if (i < a.Length && a[i] == '\r')
            {
                i++;
                continue;
            }

            if (j < b.Length && b[j] == '\r')
            {
                j++;
                continue;
            }

            if (i == a.Length || j == b.Length)
                return i == a.Length && j == b.Length;
            if (a[i] != b[j])
                return false;
            i++;
            j++;
        }
    }

    private void OnEvent(string fullPath, bool structural)
    {
        if (IsIgnoredDirectory(fullPath))
            return;
        if (IsSource(fullPath))
            _dirty[fullPath] = 0;
        else if (IsProjectFile(fullPath))
        {
            _trigger = fullPath;
            _projectFilesDirty = true;
        }
        else if (structural && !Path.HasExtension(fullPath))
        {
            // Possibly a directory; resolved at the next sync. (A Changed event on a directory only means its
            // listing changed; the files in it report themselves.)
            _structural[fullPath] = 0;
        }
    }

    /// <summary>True for files the compiler reads as sources: C#, Razor components and Razor views.</summary>
    public static bool IsSource(string path) =>
        SourceExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for files that change how projects evaluate.</summary>
    public static bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path);
        return ProjectExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
               || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsIgnoredDirectory(string path)
    {
        if (path.StartsWith(_gitHead.GitDirectory, StringComparison.OrdinalIgnoreCase))
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _blobs.Dispose();
    }
}
