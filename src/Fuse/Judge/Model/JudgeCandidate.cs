namespace Fuse.Judge.Model;

/// <summary>One candidate the judge scores: the name its verdict is ranked under and its unified diff.</summary>
/// <param name="Name">The name the verdict ranks the candidate under.</param>
/// <param name="Diff">The candidate's unified diff, relative to HEAD.</param>
internal sealed record JudgeCandidate(string Name, string Diff);
