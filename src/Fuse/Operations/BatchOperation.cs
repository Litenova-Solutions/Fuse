using System.Text.Json;
using Fuse.Client;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Operations;

/// <summary>
///     <c>fuse batch</c>: reads NDJSON <see cref="EngineRequest"/> lines from standard input and streams NDJSON
///     <see cref="EngineResponse"/> lines to standard output over one persistent engine pipe connection, eliminating
///     per-request process spawns in orchestrator launchers.
/// </summary>
internal static class BatchOperation
{
    internal const string Usage = "usage: fuse batch (reads NDJSON EngineRequest lines on stdin, writes EngineResponse lines on stdout)";

    public static async Task<OperationResult> RunAsync(RepoRoot root, IReadOnlyList<string> args, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
            return new OperationResult(Outcome.Unanswered, $"fuse: batch takes no arguments\n{Usage}");

        await EngineClient.RunBatchSessionAsync(root, async send =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                EngineRequest? request;
                try
                {
                    request = ProtocolJson.ReadRequest(line);
                }
                catch (Exception e) when (e is JsonException or NotSupportedException)
                {
                    var invalid = new EngineResponse.Unanswered(ErrorCode.Internal, $"invalid batch request line: {e.Message}");
                    await output.WriteLineAsync(ProtocolJson.Serialize(invalid)).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request is null)
                {
                    var empty = new EngineResponse.Unanswered(ErrorCode.Internal, "empty batch request");
                    await output.WriteLineAsync(ProtocolJson.Serialize(empty)).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var response = await send(request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(ProtocolJson.Serialize(response)).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);

        return new OperationResult(Outcome.Clean, "");
    }
}
