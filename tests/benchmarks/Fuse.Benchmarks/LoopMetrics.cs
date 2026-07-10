namespace Fuse.Benchmarks;

/// <summary>
///     The kind of action an agent took on one turn of a task-resolution transcript. The loop metrics (R4)
///     count the build-gated turns, so the distinction that matters is whether a turn invoked the build or test
///     verification path.
/// </summary>
public enum TurnKind
{
    /// <summary>A context-gathering read (grep, open, a Fuse read tool). Not build-gated.</summary>
    Read,

    /// <summary>An edit to the working tree. Not build-gated.</summary>
    Edit,

    /// <summary>A compile-verification turn (for example <c>dotnet build</c> or a speculative <c>fuse_check</c>).</summary>
    Build,

    /// <summary>A test-execution turn (for example <c>dotnet test</c>).</summary>
    Test,

    /// <summary>Any other turn.</summary>
    Other,
}

/// <summary>
///     One turn of a task-resolution transcript.
/// </summary>
/// <param name="Kind">The action kind.</param>
/// <param name="Passed">For a <see cref="TurnKind.Build" /> or <see cref="TurnKind.Test" /> turn, whether it succeeded; ignored otherwise.</param>
/// <param name="DurationMs">The wall-clock duration of the turn in milliseconds.</param>
public sealed record TranscriptTurn(TurnKind Kind, bool Passed, long DurationMs);

/// <summary>
///     The loop metrics computed from one task-resolution transcript: the numbers the oracle thesis moves (R4),
///     which cumulative-token counts do not capture.
/// </summary>
/// <param name="ReachedGreen">Whether a build or test turn ever passed.</param>
/// <param name="IterationsToGreen">The number of build-gated turns (build or test) up to and including the first passing one; zero when green was never reached.</param>
/// <param name="BuildInvocations">The number of build-verification turns in the session (the loop-collapse metric).</param>
/// <param name="WallClockMs">The total wall-clock of the session in milliseconds.</param>
public sealed record LoopMetricsResult(bool ReachedGreen, int IterationsToGreen, int BuildInvocations, long WallClockMs);

/// <summary>
///     Computes the loop metrics for a task-resolution transcript (R4). Deterministic and model-free, so it is
///     unit-tested against a scripted transcript and carries the claim between the expensive model-driven runs.
/// </summary>
public static class LoopMetrics
{
    /// <summary>
    ///     Computes the loop metrics for a transcript.
    /// </summary>
    /// <param name="turns">The ordered turns of the session.</param>
    /// <returns>The loop metrics.</returns>
    public static LoopMetricsResult Compute(IReadOnlyList<TranscriptTurn> turns)
    {
        var builds = turns.Count(t => t.Kind == TurnKind.Build);
        var wallClock = turns.Sum(t => t.DurationMs);

        var iterationsToGreen = 0;
        var reachedGreen = false;
        var gated = 0;
        foreach (var turn in turns)
        {
            if (turn.Kind is not (TurnKind.Build or TurnKind.Test))
                continue;
            gated++;
            if (turn.Passed)
            {
                reachedGreen = true;
                iterationsToGreen = gated;
                break;
            }
        }

        return new LoopMetricsResult(reachedGreen, reachedGreen ? iterationsToGreen : 0, builds, wallClock);
    }
}
