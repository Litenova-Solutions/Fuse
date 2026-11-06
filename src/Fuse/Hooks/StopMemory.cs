using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Hooks;

/// <summary>
///     What one session's last blocked stop found, and which files that session wrote, each kept in a file in the
///     repository's state directory. A stop that finds the same errors again, with none of them in a file the session
///     wrote after that block, lets the agent finish instead of blocking it on errors it cannot act on. An error in a
///     file the session wrote after the block blocks again, so work the session itself did is never let through.
/// </summary>
internal static partial class StopMemory
{
    /// <summary>One blocked stop: the error set it held, the tree it ended on, and when it blocked.</summary>
    /// <param name="Errors">The blocked errors, each as <see cref="KeyOf"/> writes it, in ordinal order.</param>
    /// <param name="Tree">The tree stamp the check ended on, or null when the check sent none.</param>
    /// <param name="BlockedAt">When the stop blocked, in UTC.</param>
    internal sealed record Snapshot(string[] Errors, string? Tree, DateTimeOffset BlockedAt);

    /// <summary>One error as the memory holds it: file, position, id and message, so an edit that moves or rewords an error counts as a different error.</summary>
    internal static string KeyOf(ReportedError error) =>
        $"{error.Error.Path}|{error.Error.Line}|{error.Error.Column}|{error.Error.Id}|{error.Error.Message}";

    /// <summary>The blocked stop <paramref name="session"/> last met in the state directory, or null when it met none that reads back.</summary>
    internal static Snapshot? Load(string stateDirectory, string session)
    {
        try
        {
            var path = BlockedFile(stateDirectory, session);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), SnapshotJson.Default.Snapshot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The blocked stop <paramref name="session"/> last met in <paramref name="root"/>, or null.</summary>
    internal static Snapshot? Load(RepoRoot root, string session) => Load(root.StateDirectory, session);

    /// <summary>Records <paramref name="snapshot"/> as the stop <paramref name="session"/> last met. A failure to write it is dropped: the next stop then blocks again, which is the safe side.</summary>
    internal static void Save(string stateDirectory, string session, Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(BlockedFile(stateDirectory, session), JsonSerializer.Serialize(snapshot, SnapshotJson.Default.Snapshot));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records <paramref name="snapshot"/> as the stop <paramref name="session"/> last met in <paramref name="root"/>.</summary>
    internal static void Save(RepoRoot root, string session, Snapshot snapshot) => Save(root.StateDirectory, session, snapshot);

    /// <summary>The snapshot a blocked stop records for <paramref name="errors"/> on <paramref name="tree"/> at <paramref name="blockedAt"/>.</summary>
    internal static Snapshot SnapshotFor(IReadOnlyList<ReportedError> errors, string? tree, DateTimeOffset blockedAt) =>
        new([.. errors.Select(KeyOf).Order(StringComparer.Ordinal)], tree, blockedAt);

    /// <summary>
    ///     Records that <paramref name="session"/> wrote <paramref name="relativePaths"/> at <paramref name="at"/>, one
    ///     line each in its written-files file. The lines accumulate, so a file written again after a block is told apart
    ///     from one written before it by its time.
    /// </summary>
    internal static void NoteWritten(string stateDirectory, string session, IEnumerable<string> relativePaths, DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var lines = relativePaths.Select(p => $"{at.UtcTicks}\t{p}").ToList();
            if (lines.Count > 0)
                File.AppendAllLines(WrittenFile(stateDirectory, session), lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records that <paramref name="session"/> wrote the files at <paramref name="relativePaths"/> in <paramref name="root"/>.</summary>
    internal static void NoteWritten(RepoRoot root, string session, IEnumerable<string> relativePaths) =>
        NoteWritten(root.StateDirectory, session, relativePaths, DateTimeOffset.UtcNow);

    /// <summary>
    ///     Records <paramref name="relativePaths"/> as written by <paramref name="session"/> at <paramref name="at"/>, once
    ///     per file since <paramref name="since"/>: a file that already has a written time after <paramref name="since"/>
    ///     is not recorded again, so a post-edit hook for a file the session already wrote after its last blocked stop
    ///     writes no state file. A file with no written time after <paramref name="since"/> is recorded, so a stop
    ///     still tells a file written after that block from one written before it.
    /// </summary>
    internal static void NoteWrittenOnce(string stateDirectory, string session, IEnumerable<string> relativePaths, DateTimeOffset since, DateTimeOffset at)
    {
        var paths = relativePaths.Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
            return;
        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (writtenAt, path) in ReadWritten(stateDirectory, session))
        {
            if (!latest.TryGetValue(path, out var held) || writtenAt > held)
                latest[path] = writtenAt;
        }

        var missing = paths.Where(p => !latest.TryGetValue(p, out var held) || held <= since).ToList();
        if (missing.Count > 0)
            NoteWritten(stateDirectory, session, missing, at);
    }

    /// <summary>Records <paramref name="relativePaths"/> as written by <paramref name="session"/> in <paramref name="root"/> at most once since its last blocked stop.</summary>
    internal static void NoteWrittenOnce(RepoRoot root, string session, IEnumerable<string> relativePaths)
    {
        var since = Load(root, session)?.BlockedAt ?? DateTimeOffset.MinValue;
        NoteWrittenOnce(root.StateDirectory, session, relativePaths, since, DateTimeOffset.UtcNow);
    }

    /// <summary>Every repository-relative path <paramref name="session"/> has written in the state directory, with forward slashes as written.</summary>
    internal static HashSet<string> AllWritten(string stateDirectory, string session) =>
        [.. ReadWritten(stateDirectory, session).Select(w => w.Path)];

    /// <summary>Every repository-relative path <paramref name="session"/> has written in <paramref name="root"/>.</summary>
    internal static HashSet<string> AllWritten(RepoRoot root, string session) => AllWritten(root.StateDirectory, session);

    /// <summary>The paths <paramref name="session"/> wrote after <paramref name="since"/>, each once.</summary>
    internal static HashSet<string> WrittenSince(string stateDirectory, string session, DateTimeOffset since) =>
        [.. ReadWritten(stateDirectory, session).Where(w => w.At > since).Select(w => w.Path)];

    /// <summary>The paths <paramref name="session"/> wrote in <paramref name="root"/> after <paramref name="since"/>.</summary>
    internal static HashSet<string> WrittenSince(RepoRoot root, string session, DateTimeOffset since) => WrittenSince(root.StateDirectory, session, since);

    /// <summary>
    ///     True when <paramref name="current"/> is the blocked answer <paramref name="previous"/> holds: the same error
    ///     set, the same tree where both checks stamped one, and no error in a file the session wrote after that block.
    /// </summary>
    internal static bool IsRepeat(Snapshot? previous, IReadOnlyList<ReportedError> current, string? tree, IReadOnlySet<string> writtenSince)
    {
        if (previous is null || current.Count == 0)
            return false;
        if (previous.Tree is not null && tree is not null && previous.Tree != tree)
            return false;
        var keys = current.Select(KeyOf).Order(StringComparer.Ordinal).ToList();
        if (!keys.SequenceEqual(previous.Errors.Order(StringComparer.Ordinal)))
            return false;
        return !current.Any(e => writtenSince.Contains(e.Error.Path));
    }

    /// <summary>
    ///     How many of <paramref name="current"/> the blocked answer <paramref name="previous"/> did not hold, and how
    ///     many of those sit in a file the session wrote. With no blocked answer held, every error counts as unseen.
    /// </summary>
    internal static (int Unseen, int InEditedFiles) Delta(Snapshot? previous, IReadOnlyList<ReportedError> current, IReadOnlySet<string> editedFiles)
    {
        var held = previous is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(previous.Errors, StringComparer.Ordinal);
        var unseen = current.Where(e => !held.Contains(KeyOf(e))).ToList();
        return (unseen.Count, unseen.Count(e => editedFiles.Contains(e.Error.Path)));
    }

    private static string BlockedFile(string stateDirectory, string session) => Path.Combine(stateDirectory, $"stop-blocked-{Hash(session)}.json");

    private static string WrittenFile(string stateDirectory, string session) => Path.Combine(stateDirectory, $"stop-written-{Hash(session)}.tsv");

    private static List<(DateTimeOffset At, string Path)> ReadWritten(string stateDirectory, string session)
    {
        var written = new List<(DateTimeOffset At, string Path)>();
        try
        {
            var path = WrittenFile(stateDirectory, session);
            if (!File.Exists(path))
                return written;
            foreach (var line in File.ReadAllLines(path))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1)
                    continue;
                if (long.TryParse(line[..tab], System.Globalization.CultureInfo.InvariantCulture, out var ticks))
                    written.Add((new DateTimeOffset(ticks, TimeSpan.Zero), line[(tab + 1)..]));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return written;
    }

    private static string Hash(string session) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)))[..16];

    [JsonSerializable(typeof(Snapshot))]
    private sealed partial class SnapshotJson : JsonSerializerContext
    {
    }
}
