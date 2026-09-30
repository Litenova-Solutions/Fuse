using System.Text.RegularExpressions;

namespace Fuse.Paths;

/// <summary>
///     Fuse's folders in the user's local application data: the engine copies, one per build, and a state directory per
///     repository (<see cref="RepoRoot.StateDirectory"/>). Each is removed once nothing uses it, because a new build and a
///     repository that is deleted, such as a temporary one, would otherwise each leave one behind for good.
/// </summary>
internal static partial class LocalState
{
    private const string InUsePrefix = ".in-use-";
    private const string RootFile = "root";
    private const string CleanupMarker = ".cleaned";

    /// <summary>A copy younger than this may be one a client has just made and whose engine has not started yet.</summary>
    private static readonly TimeSpan NewCopyGrace = TimeSpan.FromMinutes(10);

    /// <summary>A state directory written to within this time may still be in use, whatever its repository.</summary>
    private static readonly TimeSpan StateGrace = TimeSpan.FromDays(1);

    /// <summary>The folder holding Fuse's local state: <c>fuse</c> in the user's local application data.</summary>
    public static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "fuse");

    /// <summary>The folder holding one engine copy per build.</summary>
    public static string EngineCopies => Path.Combine(BaseDirectory, "engine");

    /// <summary>The folder holding one state directory per repository.</summary>
    public static string Repositories => Path.Combine(BaseDirectory, "repos");

    /// <summary>
    ///     Marks <paramref name="copy"/> as used by this process until the returned handle is disposed, with a lock file
    ///     that no other process can open while it is held, on Windows and on Unix alike.
    /// </summary>
    public static IDisposable HoldEngineCopy(string copy) =>
        new FileStream(Path.Combine(copy, InUsePrefix + Environment.ProcessId), FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);

    /// <summary>
    ///     Deletes each engine copy in <paramref name="engineCopies"/> other than <paramref name="keep"/> that no engine
    ///     holds and that is older than <see cref="NewCopyGrace"/>. A copy whose files cannot be deleted stays.
    /// </summary>
    public static void RemoveUnusedEngineCopies(string engineCopies, string keep, DateTime utcNow)
    {
        if (!System.IO.Directory.Exists(engineCopies))
            return;
        foreach (var copy in System.IO.Directory.EnumerateDirectories(engineCopies))
        {
            if (Path.GetFullPath(copy).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(keep).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || utcNow - System.IO.Directory.GetLastWriteTimeUtc(copy) < NewCopyGrace
                || IsHeld(copy))
                continue;
            TryDelete(copy);
        }
    }

    /// <summary>
    ///     Records which repository <paramref name="root"/>'s state directory belongs to, so a later cleanup can tell when
    ///     the repository is gone.
    /// </summary>
    public static void RecordRoot(RepoRoot root)
    {
        System.IO.Directory.CreateDirectory(root.StateDirectory);
        File.WriteAllText(Path.Combine(root.StateDirectory, RootFile), root.Path);
    }

    /// <summary>
    ///     At most once a day, deletes the state directories in <paramref name="repositories"/> whose repository no longer
    ///     exists and that nothing wrote to within <see cref="StateGrace"/>. Returns how many it deleted.
    /// </summary>
    public static int RemoveStateOfMissingRepositories(string repositories, DateTime utcNow)
    {
        var marker = Path.Combine(repositories, CleanupMarker);
        if (!System.IO.Directory.Exists(repositories) || (File.Exists(marker) && utcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1)))
            return 0;
        File.WriteAllText(marker, "");
        File.SetLastWriteTimeUtc(marker, utcNow);

        var removed = 0;
        foreach (var state in System.IO.Directory.EnumerateDirectories(repositories))
        {
            if (utcNow - LastWrite(state) < StateGrace || RootOf(state) is not { } root || System.IO.Directory.Exists(root))
                continue;
            if (TryDelete(state))
                removed++;
        }

        return removed;
    }

    /// <summary>
    ///     The repository a state directory belongs to: from its <see cref="RootFile"/>, or for a state directory an
    ///     earlier build wrote, from the engine log's start line. Null when neither says.
    /// </summary>
    internal static string? RootOf(string state)
    {
        var recorded = Path.Combine(state, RootFile);
        if (File.Exists(recorded))
            return File.ReadAllText(recorded).Trim();
        var log = Path.Combine(state, "engine.log");
        if (!File.Exists(log))
            return null;
        using var reader = new StreamReader(log);
        for (var i = 0; i < 20 && reader.ReadLine() is { } line; i++)
        {
            if (StartLine().Match(line) is { Success: true } match)
                return match.Groups["root"].Value;
        }

        return null;
    }

    private static DateTime LastWrite(string directory) =>
        System.IO.Directory.EnumerateFiles(directory).Select(File.GetLastWriteTimeUtc).Append(System.IO.Directory.GetLastWriteTimeUtc(directory)).Max();

    private static bool IsHeld(string copy)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(copy, InUsePrefix + "*"))
        {
            try
            {
                // The engine holds this file with FileShare.None; opening it succeeds only when that engine has exited.
                using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@" started for (?<root>.+) \(pid \d+\)$")]
    private static partial Regex StartLine();
}
