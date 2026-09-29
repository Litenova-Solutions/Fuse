using System.Diagnostics;

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
}
