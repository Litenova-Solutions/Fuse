using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Operations;

/// <summary>
///     <c>fuse status</c>: whether an engine runs for the repository, and how many files differ from HEAD with their
///     tree stamp. It never starts an engine: past a short wait it reads the tree from git instead.
/// </summary>
internal static partial class StatusOperation
{
    /// <summary>How long status waits for a running engine before it reads the tree from git instead.</summary>
    private static readonly TimeSpan EngineWait = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Past this many changed files a sync reloads the projects from git, which is the slowdown the warning names.
    ///     It mirrors the tracker's patch limit.
    /// </summary>
    internal const int ReloadAt = 300;

    /// <summary>
    ///     Past this many files in reach of a change a check binds whole projects, which the limits line names. It
    ///     mirrors the check's whole-project threshold.
    /// </summary>
    internal const int WholeProjectAt = 500;

    /// <param name="root">The repository to report on.</param>
    /// <param name="json">True for one JSON object instead of text.</param>
    /// <param name="cancellationToken">Cancels the wait for the answer.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, bool json, CancellationToken cancellationToken)
    {
        EngineStatus? engine = null;
        var silentEngine = false;
        if (EngineClient.PipeExists(root.PipeName))
        {
            engine = await AskAsync(root, cancellationToken).ConfigureAwait(false);
            silentEngine = engine is null;
        }

        string tree;
        int count;
        string? head;
        if (engine is not null)
        {
            tree = engine.Tree;
            count = engine.ChangedFileCount;
            head = engine.Head;
        }
        else
        {
            try
            {
                (tree, count) = await TreeStamp.ForWorkingTreeAsync(root, cancellationToken).ConfigureAwait(false);
                head = await new HeadResolver(root).ResolveAsync(null, cancellationToken).ConfigureAwait(false);
            }
            catch (FuseException e)
            {
                return new OperationResult(Outcome.Unanswered, $"fuse: {e.Message}");
            }
        }

        if (json)
        {
            var state = engine is not null ? "running" : silentEngine ? "unknown" : "stopped";
            int? memoryMB = engine is null ? null : (int)(engine.WorkingSetBytes / (1024 * 1024));
            return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(
                new StatusLine(state, engine?.Pid, memoryMB, count, head, tree, ReloadAt, WholeProjectAt),
                StatusJson.Default.StatusLine));
        }
        var text = new StringBuilder();
        text.Append(EngineLine(root, engine, silentEngine)).Append('\n');
        text.Append(TreeLine(count, tree, head)).Append('\n');
        if (count > ReloadAt)
            text.Append($"fuse: warning: {count} changed files is past the {ReloadAt}-file reload point; checks reseed from git and reload projects, so they slow down - commit soon").Append('\n');
        text.Append($"fuse: large-tree limits: past {ReloadAt} changed files a sync reloads projects; past {WholeProjectAt} files in reach a check binds whole projects; commit or run fuse stop and let the next check reload when checks feel slow");
        return new OperationResult(Outcome.Clean, text.ToString());
    }

    private static string EngineLine(RepoRoot root, EngineStatus? engine, bool silentEngine)
    {
        if (engine is not null)
            return $"fuse: engine running for {root.Path} (pid {engine.Pid}), {engine.WorkingSetBytes / (1024 * 1024)} MB; idle exits after 30 min";
        if (silentEngine)
            return $"fuse: engine for {root.Path} did not answer in {(int)EngineWait.TotalSeconds} s";
        return $"fuse: no engine runs for {root.Path}; the next check starts one";
    }

    private static string TreeLine(int count, string tree, string? head) =>
        $"fuse: {count} changed file(s) since HEAD; tree {tree}" + (head is null ? "" : $"; HEAD {head[..Math.Min(12, head.Length)]}");

    /// <summary>
    ///     Asks the running engine what the tree holds, through its pipe directly: the usual send would start an
    ///     engine when the pipe is gone, and status never starts one. Null when no answer arrives in time.
    /// </summary>
    private static async Task<EngineStatus?> AskAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(EngineWait);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var request = new EngineRequest.Status { BuildId = EngineVersion.Build, RequestId = EngineClient.NextRequestId(), WaitMs = (long)EngineWait.TotalMilliseconds };
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
                    EngineResponse.StatusAnswered { Engine: { Tree.Length: > 0 } } answered => answered.Engine,
                    EngineResponse.Unanswered unanswered => throw new FuseException(unanswered.Code, unanswered.Message ?? "the engine did not answer"),
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
            // Past the wait: the tree below is read from git.
            return null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One status answer: the engine, then the tree, then the limits.</summary>
    private sealed record StatusLine(string Engine, int? Pid, int? MemoryMB, int ChangedFiles, string? Head, string Tree, int ReloadAt, int WholeProjectAt);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StatusLine))]
    private sealed partial class StatusJson : JsonSerializerContext
    {
    }
}
