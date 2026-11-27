using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Client;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
namespace Fuse.Operations.Control;

/// <summary>
///     <c>fuse slim</c>: asks the running engine to release transient caches and run a compacting Gen2 GC,
///     returning working set to the OS before heavy builds or swarm waves. Never starts an engine.
/// </summary>
internal static partial class SlimOperation
{
    internal const string Usage = "usage: fuse slim [--json]";

    public static async Task<OperationResult> RunAsync(RepoRoot root, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var json = false;
        foreach (var token in args)
        {
            if (token == "--json")
            {
                json = true;
                continue;
            }

            return new OperationResult(Outcome.Unanswered, $"fuse: slim takes [--json]; '{token}' is not one\n{Usage}");
        }

        return await RunAsync(root, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<OperationResult> RunAsync(RepoRoot root, bool json, CancellationToken cancellationToken)
    {
        if (!EngineClient.PipeExists(root.PipeName))
        {
            if (json)
                return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(new SlimLine("stopped", 0, 0, 0, 0), SlimJson.Default.SlimLine));
            return new OperationResult(Outcome.Clean, $"fuse: no engine runs for {root.Path}; nothing to slim");
        }

        var response = await EngineClient.TryYieldMemoryAsync(root, cancellationToken).ConfigureAwait(false);
        if (response is EngineResponse.MemoryYielded yielded)
        {
            var reclaimedMB = Math.Round(yielded.ReclaimedBytes / (1024.0 * 1024.0), 1);
            var afterMB = Math.Round(yielded.WorkingSetAfterBytes / (1024.0 * 1024.0), 1);
            if (json)
            {
                return new OperationResult(
                    Outcome.Clean,
                    JsonSerializer.Serialize(new SlimLine("running", yielded.ReclaimedBytes, reclaimedMB, yielded.WorkingSetAfterBytes, afterMB), SlimJson.Default.SlimLine));
            }

            return new OperationResult(Outcome.Clean, $"fuse: engine reclaimed {reclaimedMB:0.#} MB (working set {afterMB:0.#} MB)");
        }

        if (response is EngineResponse.Acknowledged)
        {
            if (json)
                return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(new SlimLine("running", 0, 0, 0, 0), SlimJson.Default.SlimLine));
            return new OperationResult(Outcome.Clean, $"fuse: engine for {root.Path} acknowledged slim");
        }

        if (response is EngineResponse.Unanswered)
            return OperationResult.Unanswered(response, "memory yield");

        return new OperationResult(Outcome.Unanswered, $"fuse: engine for {root.Path} did not answer the slim request");
    }

    private sealed record SlimLine(string Engine, long ReclaimedBytes, double ReclaimedMB, long WorkingSetAfterBytes, double WorkingSetAfterMB);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(SlimLine))]
    private sealed partial class SlimJson : JsonSerializerContext;
}
