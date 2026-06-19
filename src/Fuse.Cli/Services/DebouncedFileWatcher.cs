namespace Fuse.Cli.Services;

/// <summary>
///     Watches a directory tree and raises debounced change notifications.
/// </summary>
public sealed class DebouncedFileWatcher : IDisposable
{
    private const int DefaultDebounceMilliseconds = 500;

    private readonly FileSystemWatcher _watcher;
    private readonly int _debounceMilliseconds;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DebouncedFileWatcher" /> class.
    /// </summary>
    /// <param name="rootDirectory">The directory to watch.</param>
    /// <param name="recursive">Whether to include subdirectories.</param>
    /// <param name="debounceMilliseconds">Debounce delay after the last filesystem event.</param>
    public DebouncedFileWatcher(string rootDirectory, bool recursive, int debounceMilliseconds = DefaultDebounceMilliseconds)
    {
        _debounceMilliseconds = debounceMilliseconds;
        _watcher = new FileSystemWatcher(Path.GetFullPath(rootDirectory))
        {
            IncludeSubdirectories = recursive,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnRenamed;
    }

    /// <summary>
    ///     Raised after filesystem changes settle for the debounce interval.
    /// </summary>
    public event Func<CancellationToken, Task>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        CancelDebounce();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath))
            return;

        ScheduleDebounce();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath) && ShouldIgnorePath(e.OldFullPath))
            return;

        ScheduleDebounce();
    }

    private void ScheduleDebounce()
    {
        CancelDebounce();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMilliseconds, cts.Token);
                if (Changed is not null)
                    await Changed(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void CancelDebounce()
    {
        if (_debounceCts is null)
            return;

        _debounceCts.Cancel();
        _debounceCts.Dispose();
        _debounceCts = null;
    }

    private static bool ShouldIgnorePath(string path)
    {
        var fuseSegment = $"{Path.DirectorySeparatorChar}.fuse{Path.DirectorySeparatorChar}";
        return path.Contains(fuseSegment, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"{Path.DirectorySeparatorChar}.fuse", StringComparison.OrdinalIgnoreCase);
    }
}
