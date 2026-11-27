namespace Fuse.Judge.Model;

/// <summary>How many candidates a judge call scores and how many bytes of diff it holds. The engine maps it from the protocol judge limits, so the numbers have one owner.</summary>
/// <param name="MaxCandidates">How many candidates one judge call scores.</param>
/// <param name="MaxDiffBytes">How many bytes of diff one candidate holds.</param>
/// <param name="MaxTotalDiffBytes">How many bytes of diff one judge call scores in total.</param>
internal sealed record JudgeBudget(int MaxCandidates, int MaxDiffBytes, int MaxTotalDiffBytes);
