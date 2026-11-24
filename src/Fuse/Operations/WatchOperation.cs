using System.Text.Json;
using Fuse.Engine.Client;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     <c>fuse watch --session &lt;name&gt;</c> and <c>fuse watch --all [--json]</c>: streams live check answers,
///     session events, and freeze guard violations over the engine pipe, falling back cleanly to polling the session
///     files in <c>sessions/*.json</c> when no engine is running or push streaming is unavailable.
/// </summary>
internal static class WatchOperation
{
    /// <summary>The usage line a watch without a session or <c>--all</c> prints, and <c>fuse watch --help</c> prints.</summary>
    internal const string Usage = "usage: fuse watch --session <name> [--json] | fuse watch --all [--json]";

    /// <summary>How often the session files are read again while falling back to polling.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Backward-compatible entrypoint for <c>fuse watch --session &lt;name&gt;</c>.</summary>
    public static Task<OperationResult> RunAsync(RepoRoot root, string? session, TextWriter output, CancellationToken cancellationToken) =>
        RunAsync(root, session, all: false, json: false, output, cancellationToken);

    /// <param name="root">The repository whose engine or state directory is watched.</param>
    /// <param name="session">The session to stream, or null when <paramref name="all"/> is true.</param>
    /// <param name="all">True to watch every session, worktree, and guard violation.</param>
    /// <param name="json">True to emit one NDJSON <see cref="WatchEvent"/> per event.</param>
    /// <param name="output">Where each event is printed as it arrives.</param>
    /// <param name="cancellationToken">Cancels the stream; the watch then ends clean.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, string? session, bool all, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        if (all && session is not null)
            return new OperationResult(Outcome.Unanswered, Usage);
        if (!all && SessionOperation.SessionFromValue(session) is null)
            return new OperationResult(Outcome.Unanswered, Usage);

        // Try live server-push NDJSON stream first whenever an engine pipe is up.
        if ((all || json || EngineClient.PipeExists(root.PipeName)) && EngineClient.PipeExists(root.PipeName))
        {
            var streamed = await EngineClient.WatchStreamAsync(
                root,
                new EngineRequest.WatchStream { Session = session, All = all },
                ev => WriteWatchEventAsync(ev, json, output, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (streamed || cancellationToken.IsCancellationRequested)
                return new OperationResult(Outcome.Clean, "");
        }

        if (all)
            return await PollAllAsync(root, json, output, cancellationToken).ConfigureAwait(false);
        return await PollSessionAsync(root, session!, json, output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OperationResult> PollSessionAsync(RepoRoot root, string session, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        var path = SessionFilePath(root.StateDirectory, session);
        string? printed = null;
        var waiting = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? content = null;
                try
                {
                    if (File.Exists(path))
                        content = File.ReadAllText(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The engine replaces the file as it writes the next answer; the next read sees it whole.
                }

                if (content is not null && content != printed)
                {
                    if (await TryEmitSessionContentAsync(content, session, json, output, cancellationToken).ConfigureAwait(false))
                    {
                        printed = content;
                        waiting = false;
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (content is null && !waiting)
                {
                    if (json)
                    {
                        var ev = new WatchEvent("waiting", Session: session, Message: $"watching session {session}; no stored answer yet");
                        await output.WriteLineAsync(JsonSerializer.Serialize(ev, ProtocolJson.Default.WatchEvent)).ConfigureAwait(false);
                    }
                    else
                    {
                        await output.WriteLineAsync($"fuse: watching session {session}; no stored answer yet").ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    waiting = true;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled by the reader: the stream ends with what it printed.
        }

        return new OperationResult(Outcome.Clean, "");
    }

    private static async Task<OperationResult> PollAllAsync(RepoRoot root, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        var sessionsDir = Path.Combine(root.StateDirectory, "sessions");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var waitingPrinted = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var emitted = false;
                if (Directory.Exists(sessionsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json").Order(StringComparer.Ordinal))
                    {
                        string? content = null;
                        try
                        {
                            content = File.ReadAllText(file);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                        }

                        if (content is null || (seen.TryGetValue(file, out var previous) && previous == content))
                            continue;
                        var fallbackName = Path.GetFileNameWithoutExtension(file);
                        if (await TryEmitSessionContentAsync(content, fallbackName, json, output, cancellationToken).ConfigureAwait(false))
                        {
                            seen[file] = content;
                            emitted = true;
                            waitingPrinted = false;
                        }
                    }

                    if (emitted)
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!emitted && seen.Count == 0 && !waitingPrinted)
                {
                    if (!json)
                        await output.WriteLineAsync("fuse: watching all sessions; no stored answer yet").ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    waitingPrinted = true;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new OperationResult(Outcome.Clean, "");
    }

    private static async Task WriteWatchEventAsync(WatchEvent ev, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(ev, ProtocolJson.Default.WatchEvent)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (ev.EventType == "guard_violation")
        {
            await output.WriteLineAsync($"fuse: guard {ev.GuardName} violation on {ev.ViolatedPattern}: {ev.Declaration ?? ev.Message}").ConfigureAwait(false);
        }
        else
        {
            foreach (var error in ev.Errors)
                await output.WriteLineAsync(error.Error.ToString()).ConfigureAwait(false);
            var who = ev.Session is { Length: > 0 } s ? $"session {s}: " : "";
            var summary = ev.Message ?? (ev.ErrorCount == 0
                ? $"no errors introduced; generation {ev.Generation}; tree {ev.Tree}"
                : $"{ev.ErrorCount} error(s) introduced; generation {ev.Generation}; tree {ev.Tree}");
            await output.WriteLineAsync($"fuse: {who}{summary}").ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The session file of <paramref name="session"/> under <paramref name="stateDirectory"/>: the same
    ///     spelling the result store in the engine writes, with the <c>registered:</c> prefix a registration
    ///     stores a session under taken off and every character a file name cannot hold replaced by <c>-</c>.
    ///     This layer may not use the engine layer that writes the files, so the spelling is held here too.
    /// </summary>
    internal static string SessionFilePath(string stateDirectory, string session)
    {
        var name = session.StartsWith("registered:", StringComparison.Ordinal) ? session["registered:".Length..] : session;
        var file = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]).Trim('.');
        return Path.Combine(stateDirectory, "sessions", (file.Length == 0 ? "session" : file) + ".json");
    }

    private static async Task<bool> TryEmitSessionContentAsync(string content, string fallbackSession, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var stored = document.RootElement;
            if (stored.ValueKind != JsonValueKind.Object || !stored.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return false;
            var session = stored.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? fallbackSession : fallbackSession;
            var generation = stored.TryGetProperty("generation", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt64() : 0;
            var tree = stored.TryGetProperty("tree", out var stamp) && stamp.ValueKind == JsonValueKind.String ? stamp.GetString() ?? "" : "";
            var count = stored.TryGetProperty("errorCount", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : errors.GetArrayLength();

            if (json)
            {
                var reported = new List<ReportedError>();
                foreach (var error in errors.EnumerateArray())
                {
                    if (ToReportedError(error) is { } re)
                        reported.Add(re);
                }

                var ev = new WatchEvent("session", session, null, generation, tree, count, [.. reported]);
                await output.WriteLineAsync(JsonSerializer.Serialize(ev, ProtocolJson.Default.WatchEvent)).ConfigureAwait(false);
                return true;
            }

            foreach (var error in errors.EnumerateArray())
                output.WriteLine(TextOf(error));
            output.WriteLine(count == 0
                ? $"fuse: session {fallbackSession}: no errors introduced; generation {generation}; tree {tree}"
                : $"fuse: session {fallbackSession}: {count} error(s) introduced; generation {generation}; tree {tree}");
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static ReportedError? ToReportedError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
            return null;
        var path = error.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var line = error.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        var col = error.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        var id = error.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
        var msg = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        var fromAnalyzer = error.TryGetProperty("fromAnalyzer", out var fa) && fa.ValueKind == JsonValueKind.True;
        return ReportedError.Create(path, line, col, id, msg, fromAnalyzer);
    }

    /// <summary>One stored error as its canonical line, from its fields or from the line the writer stored with it.</summary>
    private static string TextOf(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } line)
                return line;
            if (error.TryGetProperty("path", out var path) && error.TryGetProperty("id", out var id) && error.TryGetProperty("message", out var message))
            {
                var lineNumber = error.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                var column = error.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                return $"{path.GetString()}({lineNumber},{column}): error {id.GetString()}: {message.GetString()}";
            }
        }

        return error.ToString();
    }
}
