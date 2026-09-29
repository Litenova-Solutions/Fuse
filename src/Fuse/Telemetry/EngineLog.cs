namespace Fuse.Telemetry;

/// <summary>Append-only log file for the engine, which has no console. Rotated at 4 MB.</summary>
internal sealed class EngineLog
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly Lock _writeLock = new();

    /// <summary>Opens <c>engine.log</c> in <paramref name="directory"/>, moving a log past 4 MB aside to <c>engine.log.1</c> first.</summary>
    public EngineLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "engine.log");
        if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            File.Move(_path, _path + ".1", overwrite: true);
    }

    /// <summary>The log file's absolute path, which an internal error names so the user can find the details.</summary>
    public string FilePath => _path;

    /// <summary>Appends one timestamped line. A write that fails is dropped rather than thrown.</summary>
    public void Write(string message)
    {
        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never fail a request; a log file another process holds, or one made read-only, is skipped.
            }
        }
    }
}
