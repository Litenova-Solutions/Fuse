namespace Fuse.Evals;

/// <summary>
///     Measurements from the <c>swarm</c> eval suite: shared hypervisor memory across git worktrees, zero-restore
///     worktree check latency, <c>fuse check --brief</c>, <c>fuse gate --brief --test</c>, <c>fuse judge</c>,
///     <c>fuse batch</c>, <c>fuse graph</c>, <c>fuse slim</c>, and hardlink/MTP shadow test speed.
/// </summary>
internal sealed record SwarmResult(
    string Suite,
    string Repo,
    string Commit,
    string FuseBuild,
    string DotnetSdk,
    SwarmResult.WorktreeMeasure Worktrees,
    SwarmResult.SlimMeasure Slim,
    SwarmResult.RepairCheckMeasure CheckBrief,
    SwarmResult.GateMeasure Gate,
    SwarmResult.JudgeMeasure Judge,
    SwarmResult.BatchMeasure Batch,
    SwarmResult.GraphMeasure Graph,
    SwarmResult.ShadowMeasure Shadow,
    bool TreeCleanAfter)
{
    /// <summary>Single-worktree vs multi-worktree shared hypervisor RAM and zero-restore check latency.</summary>
    internal sealed record WorktreeMeasure(
        int SecondaryWorktrees,
        int SingleWorktreeEngineCount,
        double SingleWorktreeWorkingSetMb,
        double SingleWorktreePeakWorkingSetMb,
        int MultiWorktreeEngineCount,
        int SharedEnginePidCount,
        double MultiWorktreeWorkingSetMb,
        double MultiWorktreePeakWorkingSetMb,
        double MultiWorktreeIdleWorkingSetMb,
        double WorkingSetPerWorktreeMb,
        LatencyStats ZeroRestoreColdCheck,
        LatencyStats ZeroRestoreEditCheck);

    /// <summary>Pre-build memory yield (<c>fuse slim</c>) latency and working set drop.</summary>
    internal sealed record SlimMeasure(
        double WallMs,
        int ExitCode,
        double BeforeWorkingSetMb,
        double AfterWorkingSetMb,
        double ReclaimedMb);

    /// <summary><c>fuse check --brief</c> latency and repair hint detection on a deterministic CS1061 error.</summary>
    internal sealed record RepairCheckMeasure(
        string Target,
        LatencyStats Latency,
        int HintsFound,
        int Rounds);

    /// <summary><c>fuse gate --brief --test</c> latency on failing check and passing body-edit test rounds, with the exit code and output of each passing round that failed.</summary>
    internal sealed record GateMeasure(
        double FailCheckMs,
        int FailCheckExitCode,
        LatencyStats PassTest,
        int PassFailures,
        IReadOnlyList<string> PassFailureOutputs);

    /// <summary><c>fuse judge --combine</c> and <c>fuse judge --race</c> speculative in-memory verification.</summary>
    internal sealed record JudgeMeasure(
        LatencyStats Combine,
        bool CombinePassed,
        LatencyStats Race,
        bool RacePassed);

    /// <summary><c>fuse batch</c> throughput streaming 20 NDJSON requests over one persistent pipe process.</summary>
    internal sealed record BatchMeasure(
        int Requests,
        int Responses,
        double TotalMs,
        double PerRequestMs,
        int ExitCode);

    /// <summary><c>fuse graph --json</c> and <c>fuse impact --json</c> latency.</summary>
    internal sealed record GraphMeasure(
        LatencyStats GraphJson,
        int GraphBytes,
        LatencyStats ImpactJson,
        int ImpactBytes);

    /// <summary>Hardlink shadow mirror and MTP/VSTest shadow test speed across body edits.</summary>
    internal sealed record ShadowMeasure(
        string Target,
        LatencyStats TestRound,
        int ShadowRuns,
        int MsbuildRuns,
        PhaseStats Phases);
}
