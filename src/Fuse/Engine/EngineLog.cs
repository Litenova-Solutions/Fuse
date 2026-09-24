namespace Fuse.Engine;

/// <summary>Append-only log file for the engine, which has no console. Rotated at 4 MB.</summary>
internal sealed class EngineLog
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly Lock _gate = new();

    public EngineLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "engine.log");
        if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            File.Move(_path, _path + ".1", overwrite: true);
    }

    public string FilePath => _path;

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never fail a request.
            }
        }
    }
}
