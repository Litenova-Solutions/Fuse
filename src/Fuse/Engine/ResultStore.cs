using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Check;
using Fuse.Protocol;

namespace Fuse.Engine;

/// <summary>
///     The answers of checks by the content key each check coalesces by, in memory and, for an answer answered
///     to a session, as one file per session in the state directory (<c>sessions/&lt;name&gt;.json</c>: its errors,
///     its generation and its tree stamp). A later check whose key matches finds its answer here without a
///     reader, and <c>fuse watch --session</c> streams the session files an orchestrator reads. The store is
///     locked, so checks that run at the same time keep and take answers under the same content key each would
///     compute alone.
/// </summary>
internal sealed class ResultStore
{
    /// <summary>The most answers kept in memory; past it the answer unused the longest goes.</summary>
    private const int MaxEntries = 32;

    private readonly string _stateDirectory;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly Lock _fileLock = new();
    private readonly Dictionary<string, Entry> _answers = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Entry> _bySession = new(StringComparer.Ordinal);

    public ResultStore(string stateDirectory, Action<string>? log = null)
    {
        _stateDirectory = stateDirectory;
        _log = log;
    }

    /// <summary>The kept answer for <paramref name="key"/>, or false when nothing is kept for it.</summary>
    public bool TryGet(string key, out CheckReport report)
    {
        lock (_lock)
        {
            var found = _answers.TryGetValue(key, out var entry);
            if (found)
            {
                _order.Remove(key);
                _order.Add(key);
            }

            report = entry?.Report!;
            return found;
        }
    }

    /// <summary>The newest kept answer for <paramref name="session"/>, or false when none is kept for it.</summary>
    public bool TryGetSession(string session, out CheckReport report)
    {
        lock (_lock)
        {
            var found = _bySession.TryGetValue(session, out var entry);
            report = entry?.Report!;
            return found;
        }
    }

    /// <summary>
    ///     Keeps <paramref name="report"/> for <paramref name="key"/>, forgetting the answer unused the longest
    ///     past the cap, and writes it as the session file of <paramref name="session"/> when it names one. A
    ///     result whose analyzers did not finish is kept for nothing: it holds only what finished in time, so a
    ///     repeat runs the check again. A session keeps the answer of its newest generation: an answer of an
    ///     older generation that finishes late writes no session file over the newer one.
    /// </summary>
    /// <param name="generation">The generation the answer was pinned from.</param>
    /// <param name="tree">The tree stamp of the changed files the answer covers.</param>
    public void Keep(string key, string? session, long generation, string tree, CheckReport report)
    {
        if (report.AnalyzersUnfinished > 0)
            return;
        var kept = report with { Tree = null, TreeFiles = 0 };
        var entry = new Entry(key, session, generation, tree, kept);
        var write = false;
        lock (_lock)
        {
            if (_answers.TryGetValue(key, out var existing) && existing.Generation == generation && existing.Tree == tree)
            {
                // The waiters of one shared check keep the same answer once.
            }
            else
            {
                if (_answers.ContainsKey(key))
                    _order.Remove(key);
                else if (_order.Count >= MaxEntries)
                {
                    _answers.Remove(_order[0]);
                    _order.RemoveAt(0);
                }

                _answers[key] = entry;
                _order.Add(key);
            }

            if (session is not null && SessionEdits.IsValid(session)
                && (!_bySession.TryGetValue(session, out var newest) || generation >= newest.Generation))
            {
                _bySession[session] = entry;
                write = true;
            }
        }

        if (write)
            WriteSessionFile(entry);
    }

    /// <summary>Drops every kept answer in memory. The session files stay: they are state a reader streams, not memory the engine holds.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _answers.Clear();
            _order.Clear();
            _bySession.Clear();
        }
    }

    /// <summary>
    ///     The session file of <paramref name="session"/>: under <c>sessions</c> in the state directory, named by
    ///     the session with the <c>registered:</c> prefix a registration stores it under taken off and every
    ///     character a file name cannot hold (<c>:</c>, <c>/</c> and the rest) replaced by <c>-</c>, so a session
    ///     an orchestrator registered as <c>worker-1</c> reads at <c>sessions/worker-1.json</c> and a harness
    ///     session reads at the same spelling <c>fuse watch</c> names for it.
    /// </summary>
    public static string FileNameFor(string session)
    {
        var name = session.StartsWith(SessionEdits.RegisteredPrefix, StringComparison.Ordinal)
            ? session[SessionEdits.RegisteredPrefix.Length..]
            : session;
        var file = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]).Trim('.');
        return (file.Length == 0 ? "session" : file) + ".json";
    }

    /// <summary>The full path of the session file of <paramref name="session"/>.</summary>
    public string SessionFilePath(string session) => Path.Combine(_stateDirectory, "sessions", FileNameFor(session));

    private void WriteSessionFile(Entry entry)
    {
        var stored = new StoredAnswer(
            entry.Session!,
            entry.Generation,
            entry.Tree,
            entry.Report.ErrorCount,
            [.. entry.Report.Errors.Select(e => new StoredError(e.Error.Path, e.Error.Line, e.Error.Column, e.Error.Id, e.Error.Message, e.Error.FromAnalyzer, e.Error.ToString()))]);
        lock (_fileLock)
        {
            try
            {
                var path = SessionFilePath(entry.Session!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(stored, StoredJson.Default.StoredAnswer));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"result store: could not write the session file of {entry.Session}: {e.Message}");
            }
        }
    }

    /// <summary>One kept answer: its key, the session it was answered to, the generation and tree it covers, and its report without a tree stamp, which each reader stamps for itself.</summary>
    private sealed record Entry(string Key, string? Session, long Generation, string Tree, CheckReport Report);

    /// <summary>One introduced error as a session file holds it, with its canonical line for a reader that prints lines.</summary>
    private sealed record StoredError(string Path, int Line, int Column, string Id, string Message, bool FromAnalyzer, string Text);

    /// <summary>One session file: the session, its generation, its tree stamp, how many errors it found and the errors it sends.</summary>
    private sealed record StoredAnswer(string Session, long Generation, string Tree, int ErrorCount, StoredError[] Errors);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StoredAnswer))]
    private sealed partial class StoredJson : JsonSerializerContext
    {
    }
}
