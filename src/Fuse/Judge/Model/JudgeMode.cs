namespace Fuse.Judge.Model;

/// <summary>How the judge scores the candidates.</summary>
internal enum JudgeMode
{
    /// <summary>Applies every candidate to one fork and checks it once, reporting overlaps.</summary>
    Combine,

    /// <summary>Scores each candidate on its own fork and ranks the verdicts.</summary>
    Race,
}
