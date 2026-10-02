using Fuse.Paths;


namespace Fuse.Operations;

/// <summary>
///     One real build or test run at a time in a repository. MSBuild reads and writes the same <c>obj</c> and <c>bin</c>
///     files from every process that touches the repository, and two of them at once make one of them fail with MSB3021
///     or MSB3027 on a file the other holds open, or with CS2012 on a source file both are writing. The engine keeps no
///     part of this: a client takes the lock before it asks the engine for a plan and gives it up when its last child has
///     exited, so no two processes write build output at once.
/// </summary>
internal sealed class BuildLock : IDisposable
{
    private const int RetryMs = 200;

    /// <summary>The line a waiting client prints, once, the first time the lock is not free.</summary>
    internal const string WaitingLine = "fuse: waiting for another build or test in this repository";

    private readonly string _reason;
    private FileStream? _file;

    private BuildLock(string reason) => _reason = reason;

    /// <summary>Whether this process holds the lock file, or runs without it because the state directory cannot be written.</summary>
    public bool IsHeld => _file is not null;

    /// <summary>The reason the lock is not held, for the log; null when it is.</summary>
    public string? Reason => IsHeld ? null : _reason;

    /// <summary>
    ///     Takes the lock for <paramref name="root"/>, or returns a lock that holds nothing when the state directory
    ///     cannot be used. A repository whose state directory is read-only still builds, without the lock, and says so
    ///     in <c>hook.log</c> and the engine's log rather than failing the call.
    /// </summary>
    /// <param name="root">The repository whose builds are being serialized.</param>
    /// <param name="onWait">Called once, the first time the lock is not free, so a client can tell the user it is waiting.</param>
    public static BuildLock Acquire(RepoRoot root, Action? onWait = null)
    {
        var held = new BuildLock("");
        try
        {
            LocalState.RecordRoot(root);
            var path = Path.Combine(root.StateDirectory, "build.lock");
            var waited = false;
            while (true)
            {
                try
                {
                    // FileShare.None: the operating system, not a flag in this process, is what makes it exclusive, so a
                    // client that is killed holding it releases it at once.
                    held._file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Held by another client: wait for as long as it holds it, and say so once.
                    if (!waited)
                    {
                        waited = true;
                        onWait?.Invoke();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return new BuildLock($"the state directory {root.StateDirectory} is not writable");
                }

                Thread.Sleep(RetryMs);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BuildLock($"the lock file {Path.Combine(root.StateDirectory, "build.lock")} could not be opened: {e.Message}");
        }

        return held;
    }

    /// <summary>
    ///     Takes the lock for a command-line client: the waiting line goes to standard error once, and so does the reason
    ///     when the client has to run without the lock, so an unprotected build is never silent.
    /// </summary>
    public static BuildLock AcquireForClient(RepoRoot root)
    {
        var buildLock = Acquire(root, () => Console.Error.WriteLine(WaitingLine));
        if (buildLock.Reason is { } reason)
            Console.Error.WriteLine($"fuse: running without the per-repository build lock: {reason}");
        return buildLock;
    }

    public void Dispose() => _file?.Dispose();
}
