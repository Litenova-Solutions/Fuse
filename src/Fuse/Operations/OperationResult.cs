using System.Diagnostics;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>What a Fuse operation prints and how it ended. The same result feeds the CLI, the hooks and the MCP tools.</summary>
/// <param name="Outcome">How the operation ended; the exit code derives from it.</param>
/// <param name="Text">The complete output, already capped.</param>
internal sealed record OperationResult(Outcome Outcome, string Text)
{
    /// <summary>The process exit code: 0 clean, 1 problems found, 2 Fuse could not answer.</summary>
    public int ExitCode => Outcome switch
    {
        Outcome.Clean => 0,
        Outcome.ProblemsFound => 1,
        Outcome.Unanswered => 2,
        _ => throw new UnreachableException($"no exit code for outcome {Outcome}"),
    };

    /// <summary>
    ///     The result for an engine response that is not the answer <paramref name="expected"/> names: the engine's own
    ///     message when it could not answer, otherwise what arrived instead.
    /// </summary>
    public static OperationResult Unanswered(EngineResponse response, string expected) => new(
        Outcome.Unanswered,
        $"fuse: {(response as EngineResponse.Unanswered)?.Message ?? $"the Fuse engine sent no {expected} ({response.GetType().Name}); run the command again"}");
}
