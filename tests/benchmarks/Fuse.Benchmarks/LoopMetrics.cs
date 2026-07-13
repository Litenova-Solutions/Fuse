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
        => Compute(turns, turns.Sum(t => t.DurationMs));

    /// <summary>
    ///     Computes the loop metrics for a transcript using a separately measured end-to-end rollout duration.
    /// </summary>
    /// <param name="turns">The ordered turns of the session.</param>
    /// <param name="wallClockMs">The measured end-to-end rollout duration in milliseconds.</param>
    /// <returns>The loop metrics.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="wallClockMs" /> is negative.</exception>
    public static LoopMetricsResult Compute(IReadOnlyList<TranscriptTurn> turns, long wallClockMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wallClockMs);
        var builds = turns.Count(t => t.Kind == TurnKind.Build);
        var checks = turns.Count(t => t.Kind == TurnKind.Check);
        var tests = turns.Count(t => t.Kind == TurnKind.Test);

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
            reachedGreen, reachedGreen ? iterationsToGreen : 0, builds, checks, tests, wallClockMs);
    }
}

/// <summary>
///     Deterministic elapsed-time metrics for one loop arm. Rollout statistics describe individual observations;
///     task statistics first average repeated rollouts within each task so one task remains one cluster.
/// </summary>
/// <param name="Arm">The arm name.</param>
/// <param name="RolloutCount">The number of rollouts with a positive measured duration.</param>
/// <param name="TaskCount">The number of distinct tasks represented by those rollouts.</param>
/// <param name="TotalMs">The sum of measured rollout durations in milliseconds.</param>
/// <param name="MeanRolloutMs">The arithmetic mean duration per rollout in milliseconds.</param>
/// <param name="MedianRolloutMs">The median duration per rollout in milliseconds.</param>
/// <param name="MeanTaskMs">The arithmetic mean of per-task mean durations in milliseconds.</param>
/// <param name="MedianTaskMs">The median of per-task mean durations in milliseconds.</param>
public sealed record LoopArmTimingSummary(
    string Arm,
    int RolloutCount,
    int TaskCount,
    long TotalMs,
    double MeanRolloutMs,
    double MedianRolloutMs,
    double MeanTaskMs,
    double MedianTaskMs);

/// <summary>
///     A task-paired elapsed-time comparison between two loop arms. Each task contributes one mean duration per
///     arm, regardless of rollout count. The result is descriptive and does not claim statistical significance.
/// </summary>
/// <param name="LeftArm">The left arm name.</param>
/// <param name="RightArm">The right arm name.</param>
/// <param name="PairedTaskCount">The number of tasks with positive measured durations in both arms.</param>
/// <param name="MeanDeltaMs">The mean per-task duration difference, right minus left, in milliseconds.</param>
/// <param name="MedianDeltaMs">The median per-task duration difference, right minus left, in milliseconds.</param>
public sealed record LoopPairedTimingSummary(
    string LeftArm,
    string RightArm,
    int PairedTaskCount,
    double MeanDeltaMs,
    double MedianDeltaMs);

/// <summary>
///     A task-paired elapsed-time comparison restricted to tasks where both arms passed the gold-test oracle.
///     This prevents a fast failed rollout from being reported as a speed improvement.
/// </summary>
/// <param name="LeftArm">The baseline arm name.</param>
/// <param name="RightArm">The comparison arm name.</param>
/// <param name="VerifiedPairCount">The number of tasks with positive duration and an oracle pass in both arms.</param>
/// <param name="MeanDeltaMs">The mean per-task duration difference, right minus left, in milliseconds.</param>
/// <param name="MedianDeltaMs">The median per-task duration difference, right minus left, in milliseconds.</param>
/// <param name="MedianRelativeSavings">
///     The median per-task relative time saving, <c>(left - right) / left</c>. Positive values mean the right arm
///     completed faster. This is descriptive and carries no significance claim.
/// </param>
public sealed record LoopPairedVerifiedTimingSummary(
    string LeftArm,
    string RightArm,
    int VerifiedPairCount,
    double MeanDeltaMs,
    double MedianDeltaMs,
    double MedianRelativeSavings);

/// <summary>
///     Summarizes measured loop rollout duration by arm and compares arms over tasks observed in both arms.
/// </summary>
public static class LoopTimingMetrics
{
    /// <summary>
    ///     Computes deterministic arm summaries from canonical <see cref="TaskResult.LatencyMs" /> values.
    ///     Results with zero duration are excluded because older checkpoints did not record usable latency.
    /// </summary>
    /// <param name="results">The per-rollout task results.</param>
    /// <returns>One timing summary per arm, ordered by arm name.</returns>
    public static IReadOnlyList<LoopArmTimingSummary> SummarizeArms(IReadOnlyList<TaskResult> results)
    {
        return results
            .Where(result => result.LatencyMs > 0)
            .GroupBy(result => result.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var rollouts = group.Select(result => (double)result.LatencyMs).ToList();
                var tasks = group
                    .GroupBy(TaskId, StringComparer.Ordinal)
                    .Select(task => Metrics.Mean(task.Select(result => (double)result.LatencyMs).ToList()))
                    .ToList();
                return new LoopArmTimingSummary(
                    group.Key,
                    rollouts.Count,
                    tasks.Count,
                    group.Sum(result => result.LatencyMs),
                    Metrics.Mean(rollouts),
                    Metrics.Median(rollouts),
                    Metrics.Mean(tasks),
                    Metrics.Median(tasks));
            })
            .ToList();
    }

    /// <summary>
    ///     Computes a descriptive task-paired comparison. Repeated rollouts are averaged within each task and arm
    ///     before subtraction, and tasks missing either arm are excluded from the pair set.
    /// </summary>
    /// <param name="results">The per-rollout task results.</param>
    /// <param name="leftArm">The baseline arm name.</param>
    /// <param name="rightArm">The comparison arm name.</param>
    /// <returns>The task-paired timing summary.</returns>
    public static LoopPairedTimingSummary ComparePaired(
        IReadOnlyList<TaskResult> results,
        string leftArm,
        string rightArm)
    {
        var byTask = results
            .Where(result => result.LatencyMs > 0
                && (result.Category.Equals(leftArm, StringComparison.Ordinal)
                    || result.Category.Equals(rightArm, StringComparison.Ordinal)))
            .GroupBy(TaskId, StringComparer.Ordinal);
        var deltas = new List<double>();
        foreach (var task in byTask)
        {
            var left = task.Where(result => result.Category.Equals(leftArm, StringComparison.Ordinal)).ToList();
            var right = task.Where(result => result.Category.Equals(rightArm, StringComparison.Ordinal)).ToList();
            if (left.Count == 0 || right.Count == 0)
                continue;
            deltas.Add(
                Metrics.Mean(right.Select(result => (double)result.LatencyMs).ToList())
                - Metrics.Mean(left.Select(result => (double)result.LatencyMs).ToList()));
        }

        return new LoopPairedTimingSummary(
            leftArm,
            rightArm,
            deltas.Count,
            Metrics.Mean(deltas),
            Metrics.Median(deltas));
    }

    /// <summary>
    ///     Computes a descriptive task-paired timing comparison over verified successes only. Repeated successful
    ///     rollouts are averaged within each task and arm before subtraction.
    /// </summary>
    /// <param name="results">The per-rollout task results.</param>
    /// <param name="leftArm">The baseline arm name.</param>
    /// <param name="rightArm">The comparison arm name.</param>
    /// <returns>The verified task-paired timing summary.</returns>
    public static LoopPairedVerifiedTimingSummary ComparePairedVerified(
        IReadOnlyList<TaskResult> results,
        string leftArm,
        string rightArm)
    {
        var byTask = results
            .Where(result => result.LatencyMs > 0
                && result.OraclePassed == true
                && (result.Category.Equals(leftArm, StringComparison.Ordinal)
                    || result.Category.Equals(rightArm, StringComparison.Ordinal)))
            .GroupBy(TaskId, StringComparer.Ordinal);
        var deltas = new List<double>();
        var savings = new List<double>();
        foreach (var task in byTask)
        {
            var left = task.Where(result => result.Category.Equals(leftArm, StringComparison.Ordinal)).ToList();
            var right = task.Where(result => result.Category.Equals(rightArm, StringComparison.Ordinal)).ToList();
            if (left.Count == 0 || right.Count == 0)
                continue;

            var leftMean = Metrics.Mean(left.Select(result => (double)result.LatencyMs).ToList());
            var rightMean = Metrics.Mean(right.Select(result => (double)result.LatencyMs).ToList());
            deltas.Add(rightMean - leftMean);
            savings.Add((leftMean - rightMean) / leftMean);
        }

        return new LoopPairedVerifiedTimingSummary(
            leftArm,
            rightArm,
            deltas.Count,
            Metrics.Mean(deltas),
            Metrics.Median(deltas),
            Metrics.Median(savings));
    }

    private static string TaskId(TaskResult result)
    {
        var marker = $"/{result.Category}#";
        var markerIndex = result.Id.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex > 0 ? result.Id[..markerIndex] : result.Id;
    }
}
