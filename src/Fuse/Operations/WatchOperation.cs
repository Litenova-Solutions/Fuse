using System.Text.Json;
using Fuse.Paths;

namespace Fuse.Operations;

/// <summary>
///     <c>fuse watch --session &lt;name&gt;</c>: streams the newest stored answer for one session as the engine
///     writes it, reading <c>sessions/&lt;name&gt;.json</c> in the repository's state directory (its errors, its
///     generation and its tree stamp) and printing each answer that differs from the one before it, until it is
///     cancelled. An orchestrator runs it beside its workers, and can refuse a worker's done file while the
///     stream names errors for its session, without any worker calling Fuse. It starts no engine: with no
///     stored answer yet it waits for the first one.
/// </summary>
internal static class WatchOperation
{
    /// <summary>The usage line a watch without a session prints, and <c>fuse watch --help</c> prints.</summary>
    internal const string Usage = "usage: fuse watch --session <name>";

    /// <summary>How often the session file is read again while it streams.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <param name="root">The repository whose state directory holds the session file.</param>
    /// <param name="session">The session to stream, as the engine records it or as it was registered.</param>
    /// <param name="output">Where each stored answer is printed as it arrives.</param>
    /// <param name="cancellationToken">Cancels the stream; the watch then ends clean.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, string? session, TextWriter output, CancellationToken cancellationToken)
    {
        if (SessionOperation.SessionFromValue(session) is null)
            return new OperationResult(Outcome.Unanswered, Usage);
        var path = SessionFilePath(root.StateDirectory, session!);
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
                    if (Render(content, session!, output))
                    {
                        printed = content;
                        waiting = false;
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (content is null && !waiting)
                {
                    await output.WriteLineAsync($"fuse: watching session {session}; no stored answer yet").ConfigureAwait(false);
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

    /// <summary>Prints one stored answer and returns true, or false when it does not read yet, so the next read tries it whole.</summary>
    private static bool Render(string content, string session, TextWriter output)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var stored = document.RootElement;
            if (stored.ValueKind != JsonValueKind.Object || !stored.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return false;
            var generation = stored.TryGetProperty("generation", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt64() : 0;
            var tree = stored.TryGetProperty("tree", out var stamp) && stamp.ValueKind == JsonValueKind.String ? stamp.GetString() ?? "" : "";
            var count = stored.TryGetProperty("errorCount", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : errors.GetArrayLength();
            foreach (var error in errors.EnumerateArray())
                output.WriteLine(TextOf(error));
            output.WriteLine(count == 0
                ? $"fuse: session {session}: no errors introduced; generation {generation}; tree {tree}"
                : $"fuse: session {session}: {count} error(s) introduced; generation {generation}; tree {tree}");
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
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
