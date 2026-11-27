namespace Fuse.Judge.Model;

/// <summary>What scoring the candidates found: the ranked scores and the one-line verdict over them.</summary>
/// <param name="Mode">The judge mode that ran, <c>combine</c> or <c>race</c>.</param>
/// <param name="Clean">True when the combined overlay or winning candidate compiled with no errors introduced.</param>
/// <param name="Summary">One-line verdict summary.</param>
/// <param name="Winner">The winning candidate in race mode, or null.</param>
/// <param name="Verdicts">The ranked per-candidate scores.</param>
/// <param name="Conflicts">Merge conflicts found when combining overlapping patches.</param>
internal sealed record JudgeVerdict(
    string Mode,
    bool Clean,
    string Summary,
    string? Winner = null,
    IReadOnlyList<CandidateScore>? Verdicts = null,
    IReadOnlyList<string>? Conflicts = null)
{
    /// <summary>The ranked per-candidate scores.</summary>
    public IReadOnlyList<CandidateScore> Verdicts { get; init; } = Verdicts ?? [];

    /// <summary>Merge conflicts found when combining overlapping patches.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = Conflicts ?? [];
}
