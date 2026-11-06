using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fuse.Check.Model;
using Fuse.Paths;

namespace Fuse.Check;

/// <summary>
///     The answers of recent checks by what the tree held when they ran, so a check with nothing changed since is
///     answered without binding anything again. It lives in memory only; an engine that restarts checks again. The
///     store is locked, so checks that run at the same time keep and take answers without corrupting it, under the
///     same content key each would compute alone.
/// </summary>
internal sealed class KeptCheck
{
    /// <summary>The most answers kept; past it the answer unused the longest goes.</summary>
    private const int MaxEntries = 8;

    /// <summary>The most file hashes kept; past it the hash unused the longest goes.</summary>
    internal const int MaxFileHashEntries = 128;

    private static readonly ConcurrentDictionary<string, (long Length, long WrittenTicks, string Hash)> FileHashes = new(StringComparer.Ordinal);
    private static readonly List<string> FileHashOrder = [];
    private static readonly Lock FileHashLock = new();

    private readonly Dictionary<string, CheckResult> _answers = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Lock _lock = new();

    /// <summary>How many file hashes are kept, by absolute path.</summary>
    internal static int CachedFileHashes => FileHashes.Count;

    /// <summary>Drops every kept answer, when the memory budget sheds kept answers and caches at a request boundary. The next check of each scope runs again.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _answers.Clear();
            _order.Clear();
        }
    }

    /// <summary>The kept answer for <paramref name="key"/>, or false when nothing is kept for it.</summary>
    public bool TryGet(string key, out CheckResult result)
    {
        lock (_lock)
        {
            var found = _answers.TryGetValue(key, out var value);

            result = value!;
            return found;
        }
    }

    /// <summary>
    ///     Keeps <paramref name="result"/> for <paramref name="key"/>, forgetting the answer unused the longest past the
    ///     cap. A result whose analyzers did not finish is kept for nothing: it holds only what finished in time, so a
    ///     repeat runs the check again.
    /// </summary>
    public void Keep(string key, CheckResult result)
    {
        if (result.AnalyzersUnfinished > 0)
            return;
        lock (_lock)
        {
            if (_answers.ContainsKey(key))
            {
                _answers[key] = result;
                _order.Remove(key);
                _order.Add(key);
                return;
            }

            if (_order.Count >= MaxEntries)
            {
                _answers.Remove(_order[0]);
                _order.RemoveAt(0);
            }

            _answers[key] = result;
            _order.Add(key);
        }
    }

    /// <summary>
    ///     The key for a check of <paramref name="scopeKey"/> answered to <paramref name="session"/>, or null when a
    ///     changed file cannot be read: the check runs then instead of answering from a key that leaves a file out.
    /// </summary>
    /// <param name="scopeKey">The scope checked: all changes, or the named files in sorted order.</param>
    /// <param name="session">The session the answer is for, or null for every error.</param>
    /// <param name="compilationWide">Whether the check runs the analyzers that read the whole compilation.</param>
    /// <param name="headIdentity">The commit the baseline is taken from.</param>
    /// <param name="configurationGeneration">The workspace generation, which moves when projects are evaluated again.</param>
    /// <param name="baselineGeneration">
    ///     The baseline generation, which moves when the baseline's content does: a generated file a build wrote again
    ///     changes what HEAD is compared with, without HEAD moving.
    /// </param>
    /// <param name="sessionRecordsHash">A hash of which sessions wrote each changed file.</param>
    /// <param name="changedFiles">Every file that differs from HEAD, with its absolute path to read.</param>
    public static string? KeyFor(
        string scopeKey,
        string? session,
        bool compilationWide,
        string headIdentity,
        int configurationGeneration,
        int baselineGeneration,
        string sessionRecordsHash,
        IEnumerable<(string Relative, string Absolute)> changedFiles)
    {
        List<string> lines;
        try
        {
            lines = changedFiles
                .OrderBy(f => f.Relative, StringComparer.Ordinal)
                .Select(f => $"{f.Relative}={FileHashFor(f.Absolute)}")
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var parts = new List<string>(lines.Count + 7)
        {
            scopeKey,
            session ?? "",
            compilationWide ? "1" : "0",
            headIdentity,
            configurationGeneration.ToString(CultureInfo.InvariantCulture),
            baselineGeneration.ToString(CultureInfo.InvariantCulture),
            sessionRecordsHash,
        };
        parts.AddRange(lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }

    /// <summary>
    ///     The hex SHA-256 of the file at <paramref name="absolute"/>, kept by its absolute path with its length
    ///     and last write time in ticks. A file whose stamp is as it was when its hash was kept is not read or
    ///     hashed again. A hash is kept only when the file still has the stamp it had before the read,
    ///     so a write during the read keeps nothing. Past <see cref="MaxFileHashEntries"/> the hash unused the
    ///     longest goes, as in <see cref="ChangeReach"/>.
    /// </summary>
    private static string FileHashFor(string absolute)
    {
        var stamp = StampOf(absolute);
        lock (FileHashLock)
        {
            if (FileHashes.TryGetValue(absolute, out var kept) && kept.Length == stamp.Length && kept.WrittenTicks == stamp.WrittenTicks)
            {
                FileHashOrder.Remove(absolute);
                FileHashOrder.Add(absolute);
                return kept.Hash;
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(SharedRead.AllBytes(absolute)));
        try
        {
            if (StampOf(absolute) != stamp)
                return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return hash;
        }

        lock (FileHashLock)
        {
            FileHashes[absolute] = (stamp.Length, stamp.WrittenTicks, hash);
            FileHashOrder.Remove(absolute);
            FileHashOrder.Add(absolute);
            while (FileHashes.Count > MaxFileHashEntries && FileHashOrder.Count > 0)
            {
                var oldest = FileHashOrder[0];
                FileHashOrder.RemoveAt(0);
                FileHashes.TryRemove(oldest, out _);
            }
        }

        return hash;
    }

    /// <summary>The length and last write time in ticks of the file at <paramref name="absolute"/>.</summary>
    private static (long Length, long WrittenTicks) StampOf(string absolute)
    {
        var info = new FileInfo(absolute);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
