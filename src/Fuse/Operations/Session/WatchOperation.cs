using System.Text.Json;
using Fuse.Client;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Operations.Session;

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

        // The push stream subscribes again after a drop and ends only on cancellation (true) or when the
        // engine does not support push (false), which falls back to polling below.
        if ((all || json || EngineClient.PipeExists(root.PipeName)) && EngineClient.PipeExists(root.PipeName))
        {
            var streamed = await EngineClient.WatchStreamAsync(
                root,
                new EngineRequest.WatchStream { Session = session, All = all },
                ev => WatchRenderer.WriteWatchEventAsync(ev, json, output, cancellationToken),
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
                    if (await WatchRenderer.TryEmitSessionContentAsync(content, session, json, output, cancellationToken).ConfigureAwait(false))
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
                        if (await WatchRenderer.TryEmitSessionContentAsync(content, fallbackName, json, output, cancellationToken).ConfigureAwait(false))
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
}
