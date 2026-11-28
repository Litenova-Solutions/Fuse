using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Fuse.Client;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Operations.Session;

/// <summary>
///     <c>fuse session list [--json]</c>: the live session registrations. It asks the running engine for its status when
///     one runs and reads the registrations file directly when none does, pruning those that have ended either way.
/// </summary>
internal static class SessionListOperation
{
    internal const string Usage = "usage: fuse session list [--json]";

    /// <summary>How long the list waits for a running engine before it reads the registrations file instead.</summary>
    private static readonly TimeSpan EngineWait = TimeSpan.FromSeconds(2);

    /// <param name="root">The repository whose sessions are listed.</param>
    /// <param name="args">The words after <c>session</c>: <c>list</c> and its arguments.</param>
    /// <param name="cancellationToken">Cancels the wait for the answer.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var json = false;
        foreach (var token in args.Skip(1))
        {
            if (token == "--json")
            {
                json = true;
                continue;
            }

            return new OperationResult(Outcome.Unanswered, Usage);
        }

        var sessions = await LiveAsync(root, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(
                [.. sessions],
                ProtocolJson.Default.ReportedSessionArray));
        }

        if (sessions.Count == 0)
            return new OperationResult(Outcome.Clean, "fuse: no active sessions");
        var now = DateTimeOffset.UtcNow;
        return new OperationResult(Outcome.Clean, string.Join("\n", sessions.Select(s => FormatLine(s, now))));
    }

    /// <summary>The live sessions: from the running engine when one answers, else from the registrations file.</summary>
    internal static async Task<IReadOnlyList<ReportedSession>> LiveAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        if (EngineClient.PipeExists(root.PipeName))
        {
            var sessions = await AskAsync(root, cancellationToken).ConfigureAwait(false);
            if (sessions is not null)
                return sessions;
        }

        return SessionRegistrationReader.ReadLive(root, DateTimeOffset.UtcNow);
    }

    /// <summary>One session as the list and the status print it: the name, the tied process, the patterns and the time left.</summary>
    internal static string FormatLine(ReportedSession session, DateTimeOffset now)
    {
        var pid = session.Pid is { } tied ? tied.ToString(CultureInfo.InvariantCulture) : "none";
        return $"{session.Name}  pid={pid}  paths={string.Join(",", session.Paths)}  expires={FormatRemaining(session.ExpiresAtUtc - now)}";
    }

    /// <summary>How long <paramref name="remaining"/> lasts, rounded down to hours and minutes, minutes, or seconds.</summary>
    internal static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h{(int)remaining.Minutes}m"
            : remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m"
                : $"{(int)remaining.TotalSeconds}s";
    }

    /// <summary>
    ///     Asks the running engine what sessions are live, through its pipe directly: the usual send would start an
    ///     engine when the pipe is gone, and the list never starts one. Null when no answer arrives in time.
    /// </summary>
    private static async Task<IReadOnlyList<ReportedSession>?> AskAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using var pipeLifetime = pipe.ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(EngineWait);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var request = new EngineRequest.Status { BuildId = EngineVersion.Build, RequestId = EngineClient.NextRequestId(), WaitMs = (long)EngineWait.TotalMilliseconds, WorktreeRoot = root.Path };
            var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            var line = await PipeFraming.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
            if (line is null)
                return null;
            try
            {
                return ProtocolJson.ReadResponse(line) switch
                {
                    EngineResponse.StatusAnswered answered => answered.Engine.Sessions,
                    _ => null,
                };
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                return null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Past the wait: the registrations are read from the file instead.
            return null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
