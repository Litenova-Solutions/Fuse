using Fuse.Paths;

namespace Fuse.Check;

/// <summary>
///     Which sessions wrote each changed file, as the post-edit hooks told the engine. A file keeps every session that
///     wrote it until it matches HEAD again, so a file two sessions wrote is credited to both. The record lives in the
///     state directory, so an engine that restarts still knows who wrote what; it is never written into the repository.
/// </summary>
/// <remarks>
///     The file holds one line per file and session, the repository-relative path and the session separated by a tab. A
///     session id that holds a tab, a line break or another control character is refused by <see cref="IsValid"/>, so
///     every line reads back the way it was written. A line that does not read back is skipped.
/// </remarks>
internal sealed class SessionEdits
{
    /// <summary>The longest session id the engine records. A harness's ids are far shorter; a longer one is refused.</summary>
    public const int MaxSessionLength = 256;

    private readonly RepoRoot _root;
    private readonly string? _file;
    private readonly Lock _lock = new();
    private readonly Dictionary<RepoPath, HashSet<string>> _writers = [];

    /// <param name="root">The repository, which turns a recorded path back into a <see cref="RepoPath"/>.</param>
    /// <param name="file">The file the record is kept in, or null to keep it in memory only.</param>
    public SessionEdits(RepoRoot root, string? file)
    {
        _root = root;
        _file = file;
        Read();
    }

    /// <summary>True when <paramref name="session"/> can be recorded: not blank, at most <see cref="MaxSessionLength"/> characters, no control characters.</summary>
    public static bool IsValid(string? session) =>
        !string.IsNullOrWhiteSpace(session) && session.Length <= MaxSessionLength && !session.Any(char.IsControl);

    /// <summary>Records that <paramref name="session"/> wrote <paramref name="paths"/>.</summary>
    public void Record(string session, IEnumerable<RepoPath> paths)
    {
        lock (_lock)
        {
            var added = false;
            foreach (var path in paths)
            {
                if (!_writers.TryGetValue(path, out var sessions))
                    _writers[path] = sessions = new HashSet<string>(StringComparer.Ordinal);
                added |= sessions.Add(session);
            }

            if (added)
                Write();
        }
    }

    /// <summary>
    ///     Drops the record of every file not in <paramref name="changed"/>: such a file matches HEAD again, so the sessions
    ///     that wrote it are not credited with it when it changes again.
    /// </summary>
    /// <param name="changed">Every file that differs from HEAD now.</param>
    public void Forget(IReadOnlyCollection<RepoPath> changed)
    {
        lock (_lock)
            ForgetUnchanged(changed);
    }

    /// <summary>The sessions that wrote each of <paramref name="changed"/>, for the files any session wrote, after <see cref="Forget"/> drops the others.</summary>
    /// <param name="changed">Every file that differs from HEAD now.</param>
    public Dictionary<RepoPath, IReadOnlySet<string>> WritersOf(IReadOnlyCollection<RepoPath> changed)
    {
        lock (_lock)
        {
            ForgetUnchanged(changed);
            return _writers.ToDictionary(w => w.Key, IReadOnlySet<string> (w) => w.Value.ToHashSet(StringComparer.Ordinal));
        }
    }

    private void ForgetUnchanged(IReadOnlyCollection<RepoPath> changed)
    {
        var current = changed as IReadOnlySet<RepoPath> ?? changed.ToHashSet();
        var stale = _writers.Keys.Where(p => !current.Contains(p)).ToList();
        foreach (var path in stale)
            _writers.Remove(path);
        if (stale.Count > 0)
            Write();
    }

    private void Read()
    {
        if (_file is null || !File.Exists(_file))
            return;
        try
        {
            foreach (var line in File.ReadAllLines(_file))
            {
                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab <= 0)
                    continue;
                var session = line[(tab + 1)..];
                if (!IsValid(session))
                    continue;
                var path = _root.PathOf(line[..tab]);
                if (!_writers.TryGetValue(path, out var sessions))
                    _writers[path] = sessions = new HashSet<string>(StringComparer.Ordinal);
                sessions.Add(session);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A record that cannot be read leaves every file unattributed, which reports what a check reported before
            // sessions were recorded.
            _writers.Clear();
        }
    }

    /// <summary>Replaces the file with the record as it is now, through a temporary file so a reader never sees half of it.</summary>
    private void Write()
    {
        if (_file is null)
            return;
        try
        {
            var lines = _writers.OrderBy(w => w.Key.Relative, StringComparer.Ordinal)
                .SelectMany(w => w.Value.Order(StringComparer.Ordinal).Select(session => $"{w.Key.Relative}\t{session}"));
            var temporary = _file + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The record in memory still holds; only an engine that restarts before the next write loses it.
        }
    }
}
