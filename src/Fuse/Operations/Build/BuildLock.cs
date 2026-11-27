using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Fuse.Paths;
namespace Fuse.Operations.Build;

/// <summary>
///     Coordinates real builds and test runs in a repository. Whole-solution builds take an exclusive repository lock
///     (<c>build.lock</c>), while scoped builds over disjoint project closures take a shared repository lock plus
///     sorted per-project locks so independent subgraphs build concurrently without MSB3021/MSB3027/CS2012 collisions.
///     Callers opting into <c>--parallel-artifacts</c> (or isolating on contention) receive an isolated
///     <see cref="ArtifactsPath"/> under the state directory instead of blocking.
/// </summary>
internal sealed class BuildLock : IDisposable
{
    private const int RetryMs = 200;

    /// <summary>The line a waiting client prints, once, the first time the lock is not free.</summary>
    internal const string WaitingLine = "fuse: waiting for another build or test in this repository";

    /// <summary>At most this many artifact slots one acquisition scans while pruning; the rest wait for the next one.</summary>
    internal const int MaxSlotsScanned = 128;

    /// <summary>At most this many stale artifact slots one acquisition deletes; the rest wait for the next one.</summary>
    internal const int MaxSlotsPruned = 8;

    /// <summary>An artifact slot no build holds and nothing wrote within this time is stale and pruned on acquisition.</summary>
    internal static readonly TimeSpan StaleSlotAge = TimeSpan.FromDays(1);

    private readonly string _reason;
    private readonly List<FileStream> _handles = [];
    private FileStream? _file;
    private readonly ArtifactIsolation _isolation;

    private BuildLock(string reason, ArtifactIsolation isolation)
    {
        _reason = reason;
        _isolation = isolation;
    }

    /// <summary>Whether this process holds the lock file, or runs without it because the state directory cannot be written.</summary>
    public bool IsHeld => _file is not null || _handles.Count > 0;

    /// <summary>The reason the lock is not held, for the log; null when it is.</summary>
    public string? Reason => IsHeld ? null : _reason;

    /// <summary>
    ///     Isolated MSBuild <c>--artifacts-path</c> directory under the state directory when <c>--parallel-artifacts</c>
    ///     or contention isolation is active, or null when building into the repository's default output folders.
    /// </summary>
    public string? ArtifactsPath => _isolation switch
    {
        ArtifactIsolation.Owned owned => owned.Path,
        ArtifactIsolation.Shared shared => shared.Path,
        _ => null,
    };

    /// <summary>
    ///     Canonical deterministic lock key for a set of projects in a closure: sorted unique canonical paths hashed
    ///     to 16 lowercase hex characters, or <c>root</c> when empty.
    /// </summary>
    public static string ClosureKey(IEnumerable<string> projectPaths)
    {
        var canonical = CanonicalProjects(projectPaths);
        if (canonical.Count == 0)
            return "root";
        return Hash(string.Join('\n', canonical));
    }

    /// <summary>
    ///     Takes the exclusive whole-repository lock for <paramref name="root"/>, or returns a lock that holds nothing
    ///     when the state directory cannot be used.
    /// </summary>
    /// <param name="root">The repository whose builds are being serialized.</param>
    /// <param name="onWait">Called once, the first time the lock is not free, so a client can tell the user it is waiting.</param>
    public static BuildLock Acquire(RepoRoot root, Action? onWait = null)
    {
        var held = new BuildLock("", new ArtifactIsolation.None());
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
                    return new BuildLock($"the state directory {root.StateDirectory} is not writable", new ArtifactIsolation.None());
                }

                Thread.Sleep(RetryMs);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BuildLock($"the lock file {Path.Combine(root.StateDirectory, "build.lock")} could not be opened: {e.Message}", new ArtifactIsolation.None());
        }

        return held;
    }

    /// <summary>
    ///     Takes a DAG-aware closure lock for <paramref name="closureProjects"/>: holds <c>build.lock</c> in shared
    ///     read mode (so whole-repository builds still serialize against it) and takes sorted per-project exclusive locks
    ///     so disjoint subgraphs build concurrently. Falls back to <see cref="Acquire"/> when empty.
    /// </summary>
    public static BuildLock AcquireForProjects(RepoRoot root, IEnumerable<string> closureProjects, Action? onWait = null)
    {
        var canonical = CanonicalProjects(closureProjects);
        if (canonical.Count == 0)
            return Acquire(root, onWait);

        try
        {
            LocalState.RecordRoot(root);
            var waited = false;
            while (true)
            {
                if (TryAcquireProjectsCore(root, canonical, out var held, out var nonRetryableReason))
                    return held;
                if (nonRetryableReason is not null)
                    return new BuildLock(nonRetryableReason, new ArtifactIsolation.None());
                if (!waited)
                {
                    waited = true;
                    onWait?.Invoke();
                }

                Thread.Sleep(RetryMs);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BuildLock($"the closure lock in {root.StateDirectory} could not be opened: {e.Message}", new ArtifactIsolation.None());
        }
    }

    /// <summary>
    ///     Attempts to acquire the DAG-aware closure lock once without waiting. Returns true when acquired; false when
    ///     contended or on an unwritable directory.
    /// </summary>
    public static bool TryAcquireForProjects(RepoRoot root, IEnumerable<string> closureProjects, out BuildLock? held)
    {
        var canonical = CanonicalProjects(closureProjects);
        if (canonical.Count == 0)
        {
            held = null;
            return false;
        }

        try
        {
            LocalState.RecordRoot(root);
            return TryAcquireProjectsCore(root, canonical, out held, out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            held = null;
            return false;
        }
    }

    /// <summary>
    ///     Allocates an isolated <c>--artifacts-path</c> folder under <paramref name="root"/>'s state directory and
    ///     holds a lock file inside it so parallel builds never touch the shared repository <c>bin</c>/<c>obj</c> trees.
    ///     A unique slot is deleted when its lock is released and no build holds it; acquiring also prunes stale
    ///     slots, so a killed build leaves nothing behind for good.
    /// </summary>
    public static BuildLock AcquireIsolated(RepoRoot root, string? workerKey = null)
    {
        // A slot named for a worker key is shared by design, so it is never owned; a unique one is this lock's to delete.
        var unique = string.IsNullOrWhiteSpace(workerKey);
        var slot = unique
            ? $"{Environment.ProcessId}-{Guid.NewGuid():N}"[..16]
            : Hash(workerKey ?? throw new InvalidOperationException("a shared artifact slot needs a worker key"));
        var parent = Path.Combine(root.StateDirectory, "artifacts");
        var artifacts = Path.Combine(parent, slot);
        try
        {
            LocalState.RecordRoot(root);
            Directory.CreateDirectory(parent);
            PruneStaleSlots(parent);
            Directory.CreateDirectory(artifacts);
            var lockPath = Path.Combine(artifacts, "artifacts.lock");
            ArtifactIsolation isolation = unique ? new ArtifactIsolation.Owned(artifacts) : new ArtifactIsolation.Shared(artifacts);
            var held = new BuildLock("", isolation)
            {
                _file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            };
            return held;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BuildLock($"the isolated artifacts directory {artifacts} could not be locked: {e.Message}", new ArtifactIsolation.Shared(artifacts));
        }
    }

    /// <summary>
    ///     Acquires a lock for a CLI client over <paramref name="closureProjects"/>, using an isolated artifacts folder
    ///     when <paramref name="parallelArtifacts"/> is true (or when <paramref name="isolateOnContention"/> is true and
    ///     the closure lock is currently held by another process).
    /// </summary>
    public static BuildLock AcquireForProjectsForClient(
        RepoRoot root,
        IReadOnlyList<string> closureProjects,
        bool parallelArtifacts = false,
        bool isolateOnContention = false)
    {
        if (parallelArtifacts)
        {
            var isolated = AcquireIsolated(root);
            if (isolated.Reason is { } isoReason)
                Console.Error.WriteLine($"fuse: running without isolated build lock: {isoReason}");
            return isolated;
        }

        if (isolateOnContention && closureProjects.Count > 0)
        {
            if (TryAcquireForProjects(root, closureProjects, out var immediate) && immediate is not null)
                return immediate;
            var isolated = AcquireIsolated(root);
            Console.Error.WriteLine($"fuse: another build holds the project lock; isolating output to {isolated.ArtifactsPath}");
            return isolated;
        }

        var buildLock = closureProjects.Count > 0
            ? AcquireForProjects(root, closureProjects, () => Console.Error.WriteLine(WaitingLine))
            : Acquire(root, () => Console.Error.WriteLine(WaitingLine));
        if (buildLock.Reason is { } reason)
            Console.Error.WriteLine($"fuse: running without the per-repository build lock: {reason}");
        return buildLock;
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

    private static bool TryAcquireProjectsCore(RepoRoot root, List<string> canonical, [NotNullWhen(true)] out BuildLock? held, out string? nonRetryableReason)
    {
        held = null;
        nonRetryableReason = null;
        var rootLockPath = Path.Combine(root.StateDirectory, "build.lock");
        var locksDir = Path.Combine(root.StateDirectory, "locks");
        Directory.CreateDirectory(locksDir);

        // If build.lock is a directory (e.g. unwritable/blocked test scenario), fail non-retryably.
        if (Directory.Exists(rootLockPath))
        {
            nonRetryableReason = $"the lock file {rootLockPath} is a directory";
            return false;
        }

        var attempt = new BuildLock("", new ArtifactIsolation.None());
        try
        {
            // Ensure build.lock exists without requiring Write share if an exclusive holder already created it.
            if (!File.Exists(rootLockPath))
            {
                try
                {
                    using var created = new FileStream(rootLockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
                catch (IOException)
                {
                    // Another process created or locked it first.
                }
            }

            // Shared read on build.lock: concurrent closure builds share it, while a whole-solution Acquire (FileShare.None)
            // cannot coexist with it.
            attempt._handles.Add(new FileStream(rootLockPath, FileMode.Open, FileAccess.Read, FileShare.Read));

            // Acquire per-project locks in canonical sorted order so overlapping closures cannot deadlock.
            foreach (var project in canonical)
            {
                var lockFile = Path.Combine(locksDir, $"proj-{Hash(project)}.lock");
                attempt._handles.Add(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }

            held = attempt;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            attempt.Dispose();
            nonRetryableReason = $"the state directory {root.StateDirectory} is not writable";
            return false;
        }
        catch (IOException)
        {
            attempt.Dispose();
            return false;
        }
    }

    private static List<string> CanonicalProjects(IEnumerable<string> projectPaths)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in projectPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var normalized = Path.GetFullPath(raw).Replace('\\', '/');
            if (OperatingSystem.IsWindows())
                normalized = normalized.ToUpperInvariant();
            set.Add(normalized);
        }

        var sorted = set.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>
    ///     Deletes the artifact slots under <paramref name="parent"/> that no build holds and nothing wrote within
    ///     <see cref="StaleSlotAge"/>, scanning at most <see cref="MaxSlotsScanned"/> and deleting at most
    ///     <see cref="MaxSlotsPruned"/>. A killed build's slot is unheld, so the next acquisition removes it. It never
    ///     throws: cleanup never breaks the build it runs beside.
    /// </summary>
    private static void PruneStaleSlots(string parent)
    {
        List<string> entries;
        try
        {
            entries = [.. Directory.EnumerateDirectories(parent).OrderBy(entry => entry, StringComparer.Ordinal).Take(MaxSlotsScanned)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var pruned = 0;
        foreach (var entry in entries)
        {
            if (pruned >= MaxSlotsPruned)
                return;
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(entry) < StaleSlotAge || SlotHeld(entry))
                    continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (TryDeleteDirectory(entry))
                pruned++;
        }
    }

    /// <summary>True while a build holds the artifact slot <paramref name="slot"/>: its lock file then opens for nothing else.</summary>
    private static bool SlotHeld(string slot)
    {
        var lockPath = Path.Combine(slot, "artifacts.lock");
        if (!File.Exists(lockPath))
            return false;
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Deletes <paramref name="directory"/> with its content; false when anything in it could not be deleted.</summary>
    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Releases the lock, and deletes an owned artifact slot nothing holds.</summary>
    public void Dispose()
    {
        var held = _file is not null;
        _file?.Dispose();
        _file = null;
        foreach (var handle in _handles)
            handle.Dispose();
        _handles.Clear();
        if (_isolation is ArtifactIsolation.Owned owned && held && !SlotHeld(owned.Path))
            TryDeleteDirectory(owned.Path);
    }
}
