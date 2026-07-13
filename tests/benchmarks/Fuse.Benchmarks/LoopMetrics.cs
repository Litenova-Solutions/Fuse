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

    /// <summary>
    ///     An agent-visible compile round-trip: a real <c>dotnet build</c> (or <c>dotnet run</c>) the agent
    ///     invoked itself. Kept distinct from <see cref="Check" /> so the loop-collapse metric counts real build
    ///     round-trips separately from speculative checks (the first B1 harness gap, D22a).
    /// </summary>
    Build,

    /// <summary>
    ///     A speculative <c>fuse_check</c> typecheck. A verification turn (it counts toward reaching green), but
    ///     NOT an agent-visible build round-trip, so it is counted in its own column, never folded into
    ///     <see cref="Build" /> (D22a).
    /// </summary>
    Check,

    /// <summary>A test-execution turn (for example <c>dotnet test</c>).</summary>
    Test,

    /// <summary>Any other turn.</summary>
    Other,
}

/// <summary>
///     One turn of a task-resolution transcript.
/// </summary>
/// <param name="Kind">The action kind.</param>
/// <param name="Passed">For a verification turn (<see cref="TurnKind.Build" />, <see cref="TurnKind.Check" />, or <see cref="TurnKind.Test" />), whether it succeeded; ignored otherwise.</param>
/// <param name="DurationMs">The wall-clock duration of the turn in milliseconds.</param>
public sealed record TranscriptTurn(TurnKind Kind, bool Passed, long DurationMs);

/// <summary>
///     The loop metrics computed from one task-resolution transcript: the numbers the oracle thesis moves (R4),
///     which cumulative-token counts do not capture. Agent-visible build round-trips are counted separately from
///     speculative <c>fuse_check</c> turns (D22a), so the loop-collapse metric is not confounded by the tool that
///     is supposed to collapse it.
/// </summary>
/// <param name="ReachedGreen">
///     Whether any verification turn (a real build, a <c>dotnet test</c>, or a speculative <c>fuse_check</c>) ever
///     passed. This is the transcript-derived proxy for pass@1; the true pass is the oracle post-check (D22a).
/// </param>
/// <param name="IterationsToGreen">The number of verification turns (build, check, or test) up to and including the first passing one; zero when green was never reached.</param>
/// <param name="BuildInvocations">The number of agent-visible build round-trips (<c>dotnet build</c>/<c>run</c>), the loop-collapse metric, NOT including speculative checks.</param>
/// <param name="CheckInvocations">The number of speculative <c>fuse_check</c> turns, counted in their own column so they never inflate the build column (D22a).</param>
/// <param name="TestInvocations">The number of agent-visible <c>dotnet test</c> turns.</param>
/// <param name="WallClockMs">The total wall-clock of the session in milliseconds.</param>
public sealed record LoopMetricsResult(
    bool ReachedGreen, int IterationsToGreen, int BuildInvocations, int CheckInvocations, int TestInvocations, long WallClockMs)
{
    /// <summary>
    ///     The agent-visible verification round-trips (real build plus real test), the honest denominator for the
    ///     "fuse should collapse build round-trips" claim now that speculative checks are counted separately.
    /// </summary>
    public int AgentVisibleVerifications => BuildInvocations + TestInvocations;
}

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
        var checks = turns.Count(t => t.Kind == TurnKind.Check);
        var tests = turns.Count(t => t.Kind == TurnKind.Test);
        var wallClock = turns.Sum(t => t.DurationMs);

        // A verification turn is a build, a test, or a speculative check; the proxy "reached green" is the first
        // one that passed. (The oracle post-check, computed by the suite, is the true pass@1; this is the proxy.)
        var iterationsToGreen = 0;
        var reachedGreen = false;
        var gated = 0;
        foreach (var turn in turns)
        {
            if (turn.Kind is not (TurnKind.Build or TurnKind.Check or TurnKind.Test))
                continue;
            gated++;
            if (turn.Passed)
            {
                reachedGreen = true;
                iterationsToGreen = gated;
                break;
            }
        }

        return new LoopMetricsResult(
            reachedGreen, reachedGreen ? iterationsToGreen : 0, builds, checks, tests, wallClock);
    }
}
