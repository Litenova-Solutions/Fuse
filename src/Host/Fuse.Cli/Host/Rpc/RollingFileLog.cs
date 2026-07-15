using Fuse.Reduction.Caching;

namespace Fuse.Cli.Rpc;

/// <summary>
///     A small size-rotating log file for the daemon (R37), at a known path under the user-data directory, so a
///     daemon's lifecycle and degraded states are recoverable after the fact rather than lost to a detached
///     stderr. Best-effort: a logging failure never disrupts the daemon.
/// </summary>
public static class RollingFileLog
{
    /// <summary>The size (bytes) at which the active log is rotated to <c>.1</c>.</summary>
    public const long RotateAtBytes = 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>The absolute path to the active daemon log file (<c>{user-data}/logs/fuse-host.log</c>).</summary>
    /// <returns>The log file path.</returns>
    public static string LogPath() => Path.Combine(FuseStorePaths.GetUserDataDirectory(), "logs", "fuse-host.log");

    /// <summary>
    ///     Appends a timestamped line to the daemon log, rotating the file to <c>.1</c> first when it exceeds
    ///     <see cref="RotateAtBytes" />. Best-effort; a failure is swallowed.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public static void Write(string message)
    {
        try
        {
            var path = LogPath();
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded(path);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < RotateAtBytes)
            return;

        var rolled = path + ".1";
        try
        {
            if (File.Exists(rolled))
                File.Delete(rolled);
            File.Move(path, rolled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
