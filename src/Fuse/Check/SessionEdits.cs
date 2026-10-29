using System.Security.Cryptography;
using System.Text;
using Fuse.Failures;
using Fuse.Paths;

namespace Fuse.Check;

/// <summary>
///     Which sessions wrote each changed file, as the post-edit hooks told the engine. A file keeps every session that
///     wrote it until it matches HEAD again, so a file two sessions wrote is credited to both. The record lives in the
///     state directory, so an engine that restarts still knows who wrote what; it is never written into the repository.
/// </summary>
/// <remarks>
///     <para>
///         The file holds one line per file and session, the repository-relative path and the session separated by a tab,
///         and one line per stopped session, a tab followed by the session. A session id that holds a tab, a line break
///         or another control character is refused by <see cref="IsValid"/>, so every line reads back the way it was
///         written. A line that does not read back is skipped.
///     </para>
///     <para>
///         A session has stopped once its stop hook asked for a check (<see cref="MarkStopped"/>). A subagent that has
///         stopped writes no more, so its parent session counts its edits as its own (<see cref="IsOwnEdit"/>), while a
///         subagent that is still running is left to finish its own work.
///     </para>
///     <para>
///         An external writer a launcher registered (<see cref="RegisteredSession"/>) is an ordinary writer in
///         <see cref="WritersOf"/>: a changed file under its patterns gains <c>registered:</c> followed by its name. Its
///         record lives in a second file in the state directory, so it survives an engine restart, and it expires on its
///         own.
///     </para>
/// </remarks>
internal sealed class SessionEdits
{
    /// <summary>The longest session id the engine records. A harness's ids are far shorter; a longer one is refused.</summary>
    public const int MaxSessionLength = 256;

    /// <summary>What a registered writer's stored id starts with, followed by the name the launcher gave.</summary>
    public const string RegisteredPrefix = "registered:";

    /// <summary>The longest name a registration accepts.</summary>
    public const int MaxRegistrationNameLength = 64;

    /// <summary>The most patterns one registration holds.</summary>
    public const int MaxPatternsPerRegistration = 32;

    /// <summary>The most registrations that are live at once.</summary>
    public const int MaxLiveRegistrations = 64;

    /// <summary>How long a registration lasts without being begun again: long enough that an idle engine dying does not end it.</summary>
    public static readonly TimeSpan RegistrationLifetime = TimeSpan.FromHours(4);

    private readonly RepoRoot _root;
    private readonly string? _file;
    private readonly string? _registrationsFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<RepoPath, HashSet<string>> _writers = [];
    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);
    private readonly List<RegisteredSession> _registrations = [];

    /// <param name="root">The repository, which turns a recorded path back into a <see cref="RepoPath"/>.</param>
    /// <param name="file">The file the record is kept in, or null to keep it in memory only.</param>
    /// <param name="registrationsFile">The file the registrations are kept in, or null to keep them in memory only.</param>
    /// <param name="clock">What time it is, in UTC; tests move it past an expiry.</param>
    /// <param name="log">Where an expiry is logged, or null to log nowhere.</param>
    public SessionEdits(RepoRoot root, string? file, string? registrationsFile = null, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        _root = root;
        _file = file;
        _registrationsFile = registrationsFile;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        Read();
        ReadRegistrations();
    }

    /// <summary>True when <paramref name="session"/> can be recorded: not blank, at most <see cref="MaxSessionLength"/> characters, no control characters.</summary>
    public static bool IsValid(string? session) =>
        !string.IsNullOrWhiteSpace(session) && session.Length <= MaxSessionLength && !session.Any(char.IsControl);

    /// <summary>
    ///     True when <paramref name="name"/> can be registered: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or <c>.</c>. No
    ///     slash or colon, so a registration never aliases a harness session or acquires subagent semantics.
    /// </summary>
    public static bool IsValidRegistrationName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxRegistrationNameLength
        && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

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

    /// <summary>Records that <paramref name="session"/> has stopped, so its parent session counts its edits as its own.</summary>
    public void MarkStopped(string session)
    {
        lock (_lock)
        {
            if (_stopped.Add(session))
                Write();
        }
    }

    /// <summary>
    ///     True when <paramref name="writer"/>'s edits count as <paramref name="session"/>'s own: it is the session, or a
    ///     subagent of it that has stopped. A subagent's id is its parent's followed by a slash and the subagent's own.
    /// </summary>
    public bool IsOwnEdit(string writer, string session)
    {
        if (string.Equals(writer, session, StringComparison.Ordinal))
            return true;
        if (writer.Length <= session.Length + 1 || writer[session.Length] != '/' || !writer.StartsWith(session, StringComparison.Ordinal))
            return false;
        lock (_lock)
            return _stopped.Contains(writer);
    }

    /// <summary>
    ///     Registers <paramref name="name"/> for <paramref name="patterns"/>, replacing a live registration of the same name
    ///     and restarting its 4 hours. The patterns are repository-relative, as <see cref="PathPattern"/> matches them.
    /// </summary>
    /// <exception cref="FuseException">The name or a pattern is not valid, or too many sessions are registered.</exception>
    public void BeginRegistration(string name, IReadOnlyList<string> patterns)
    {
        if (!IsValidRegistrationName(name))
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.InvalidSessionName(name));
        if (patterns.Count == 0)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.SessionNeedsPattern());
        if (patterns.Count > MaxPatternsPerRegistration)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.TooManySessionPatterns(patterns.Count));
        var normalized = new List<string>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (!PathPattern.Normalize(pattern, out var clean))
                throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.InvalidSessionPattern(pattern));
            normalized.Add(clean);
        }

        lock (_lock)
        {
            PruneExpired();
            var session = RegisteredPrefix + name;
            var known = _registrations.FindIndex(r => r.Session == session);
            if (known < 0 && _registrations.Count >= MaxLiveRegistrations)
                throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.TooManyRegisteredSessions());
            var registration = new RegisteredSession(session, normalized, _clock().ToUniversalTime() + RegistrationLifetime);
            if (known >= 0)
                _registrations[known] = registration;
            else
                _registrations.Add(registration);
            WriteRegistrations();
        }
    }

    /// <summary>Ends the registration <paramref name="name"/>, or false when none is live under it.</summary>
    public bool EndRegistration(string name)
    {
        lock (_lock)
        {
            PruneExpired();
            var removed = _registrations.RemoveAll(r => r.Session == RegisteredPrefix + name) > 0;
            if (removed)
                WriteRegistrations();
            return removed;
        }
    }

    /// <summary>The registrations that have not expired, after pruning those that have.</summary>
    public IReadOnlyList<RegisteredSession> LiveRegistrations()
    {
        lock (_lock)
        {
            PruneExpired();
            return [.. _registrations];
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

    /// <summary>A hash of the record as it is now, in the order the file keeps it, so a kept answer is answered again only while the record reads the same.</summary>
    internal string RecordsHash()
    {
        lock (_lock)
        {
            PruneExpired();
            var lines = _writers.OrderBy(w => w.Key.Relative, StringComparer.Ordinal)
                .SelectMany(w => w.Value.Order(StringComparer.Ordinal).Select(session => $"{w.Key.Relative}\t{session}"))
                .Concat(_stopped.Order(StringComparer.Ordinal).Select(session => $"\t{session}"))
                .Concat(_registrations.OrderBy(r => r.Session, StringComparer.Ordinal)
                    .Select(r => $"{r.Session}\t{r.Expires.UtcTicks}\t{string.Join("\t", r.Patterns.Order(StringComparer.Ordinal))}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
    }

    /// <summary>The sessions that wrote each of <paramref name="changed"/>, for the files any session wrote, after <see cref="Forget"/> drops the others.</summary>
    /// <param name="changed">Every file that differs from HEAD now.</param>
    public Dictionary<RepoPath, IReadOnlySet<string>> WritersOf(IReadOnlyCollection<RepoPath> changed)
    {
        lock (_lock)
        {
            PruneExpired();
            ForgetUnchanged(changed);
            var writers = _writers.ToDictionary(w => w.Key, IReadOnlySet<string> (w) => w.Value.ToHashSet(StringComparer.Ordinal));
            foreach (var path in changed)
            {
                foreach (var registration in _registrations)
                {
                    if (registration.Patterns.Any(p => PathPattern.Matches(p, path.Relative)))
                    {
                        if (!writers.TryGetValue(path, out var sessions))
                            writers[path] = sessions = new HashSet<string>(StringComparer.Ordinal);
                        ((HashSet<string>)sessions).Add(registration.Session);
                    }
                }
            }

            return writers;
        }
    }

    /// <summary>
    ///     The changed files whose writers are all other sessions than <paramref name="session"/>: own files, jointly written
    ///     files, and files no session is credited with are all kept. What a stop check and a session's test plan leave out.
    /// </summary>
    public HashSet<RepoPath> OnlyOtherSessionsWrote(IReadOnlyCollection<RepoPath> changed, string session)
    {
        var writers = WritersOf(changed);
        if (!writers.Values.Any(sessions => sessions.Any(writer => IsOwnEdit(writer, session))))
            return [];
        return writers.Where(w => w.Value.Count > 0 && !w.Value.Any(writer => IsOwnEdit(writer, session)))
            .Select(w => w.Key)
            .ToHashSet();
    }

    private void ForgetUnchanged(IReadOnlyCollection<RepoPath> changed)
    {
        var current = changed as IReadOnlySet<RepoPath> ?? changed.ToHashSet();
        var stale = _writers.Keys.Where(p => !current.Contains(p)).ToList();
        foreach (var path in stale)
            _writers.Remove(path);
        // A stopped session that no file is credited to any more has nothing left to count.
        var gone = _stopped.RemoveWhere(s => !_writers.Values.Any(w => w.Contains(s)));
        if (stale.Count > 0 || gone > 0)
            Write();
    }

    /// <summary>Drops every expired registration, logging each once. The caller holds the lock.</summary>
    private void PruneExpired()
    {
        var now = _clock();
        var expired = _registrations.Where(r => r.Expires <= now).ToList();
        if (expired.Count == 0)
            return;
        foreach (var registration in expired)
        {
            _registrations.Remove(registration);
            _log?.Invoke($"registered session {registration.Session[RegisteredPrefix.Length..]} expired; its files count as no session's now");
        }

        WriteRegistrations();
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
                if (tab < 0)
                    continue;
                var session = line[(tab + 1)..];
                if (!IsValid(session))
                    continue;
                if (tab == 0)
                {
                    _stopped.Add(session);
                    continue;
                }

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
            _stopped.Clear();
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
                .SelectMany(w => w.Value.Order(StringComparer.Ordinal).Select(session => $"{w.Key.Relative}\t{session}"))
                .Concat(_stopped.Order(StringComparer.Ordinal).Select(session => $"\t{session}"));
            var temporary = _file + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The record in memory still holds; only an engine that restarts before the next write loses it.
        }
    }

    private void ReadRegistrations()
    {
        if (_registrationsFile is null || !File.Exists(_registrationsFile))
            return;
        try
        {
            foreach (var line in File.ReadAllLines(_registrationsFile))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3 || !long.TryParse(parts[0], out var ticks) || !IsValidRegistrationName(parts[1]))
                    continue;
                var patterns = new List<string>();
                var valid = true;
                foreach (var pattern in parts[2..])
                {
                    if (!PathPattern.Normalize(pattern, out var clean))
                    {
                        valid = false;
                        break;
                    }

                    patterns.Add(clean);
                }

                if (!valid || patterns.Count == 0 || patterns.Count > MaxPatternsPerRegistration)
                    continue;
                _registrations.RemoveAll(r => r.Session == RegisteredPrefix + parts[1]);
                _registrations.Add(new RegisteredSession(RegisteredPrefix + parts[1], patterns, new DateTimeOffset(ticks, TimeSpan.Zero)));
                if (_registrations.Count > MaxLiveRegistrations)
                    _registrations.RemoveAt(0);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A record that cannot be read credits no file to a registered writer, which reports what a check reported
            // before anything was registered.
            _registrations.Clear();
        }
    }

    /// <summary>Replaces the registrations file with the registrations as they are now. The caller holds the lock.</summary>
    private void WriteRegistrations()
    {
        if (_registrationsFile is null)
            return;
        try
        {
            var lines = _registrations.OrderBy(r => r.Session, StringComparer.Ordinal)
                .Select(r => $"{r.Expires.UtcTicks}\t{r.Session[RegisteredPrefix.Length..]}\t{string.Join("\t", r.Patterns)}");
            var temporary = _registrationsFile + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, _registrationsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The record in memory still holds; only an engine that restarts before the next write loses it.
        }
    }
}
