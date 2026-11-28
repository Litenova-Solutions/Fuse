using Fuse.Protocol.Check;
namespace Fuse.Protocol.Analysis;

/// <summary>Result of <c>fuse judge --combine</c> or <c>fuse judge --race</c>.</summary>
/// <param name="Mode">The judge mode that ran (<c>combine</c> or <c>race</c>).</param>
/// <param name="Clean">True when the combined overlay or winning candidate compiled with zero errors.</param>
/// <param name="Summary">One-line verdict summary.</param>
/// <param name="Winner">Name of the winning candidate in race mode, or null.</param>
/// <param name="Verdicts">Ranked per-candidate evaluation results.</param>
/// <param name="Conflicts">Merge conflicts detected when combining overlapping patches.</param>
internal sealed record JudgeReport(
    string Mode,
    bool Clean,
    string Summary,
    string? Winner = null,
    JudgeReport.CandidateVerdict[]? Verdicts = null,
    string[]? Conflicts = null)
{
    /// <summary>Ranked per-candidate evaluation results. Empty when none were evaluated.</summary>
    public CandidateVerdict[] Verdicts { get; init; } = Verdicts ?? [];

    /// <summary>Merge conflicts detected in combine mode. Empty when patches applied cleanly.</summary>
    public string[] Conflicts { get; init; } = Conflicts ?? [];

    /// <summary>Scorecard entry for one candidate or combined overlay.</summary>
    /// <param name="Name">Candidate name.</param>
    /// <param name="Rank">One-based rank (1 is best).</param>
    /// <param name="Compiles">True when zero compiler errors were introduced.</param>
    /// <param name="ErrorCount">Total introduced compiler and analyzer errors.</param>
    /// <param name="AnalyzerErrorCount">Analyzer errors among <paramref name="ErrorCount"/>.</param>
    /// <param name="SurfaceBreaks">Public or cross-project declaration breaks detected.</param>
    /// <param name="ElapsedMs">Evaluation duration in milliseconds.</param>
    /// <param name="Errors">Errors introduced by this candidate.</param>
    /// <param name="ConflictReason">Patch apply failure reason, or null.</param>
    public sealed record CandidateVerdict(
        string Name,
        int Rank,
        bool Compiles,
        int ErrorCount,
        int AnalyzerErrorCount = 0,
        int SurfaceBreaks = 0,
        long ElapsedMs = 0,
        ReportedError[]? Errors = null,
        string? ConflictReason = null)
    {
        /// <summary>Errors introduced by this candidate.</summary>
        public ReportedError[] Errors { get; init; } = Errors ?? [];
    }
}
