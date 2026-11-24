namespace Fuse.Protocol;

/// <summary>
///     Input for <c>fuse judge</c>: evaluates worker patches or worktree diffs in memory without modifying the
///     primary working tree on disk.
/// </summary>
/// <param name="Mode">Whether to combine all patches into one overlay or race candidates independently.</param>
/// <param name="Candidates">The candidate patches or worktree diffs to evaluate.</param>
/// <param name="RunTests">True to run affected tests for compiling candidates.</param>
/// <param name="MaxFailures">Maximum test failures per candidate before stopping early; 0 uses the default.</param>
internal sealed record JudgeRequest(
    JudgeRequest.JudgeMode Mode,
    JudgeRequest.Candidate[] Candidates,
    bool RunTests = false,
    int MaxFailures = 0)
{
    /// <summary>The candidate patches or worktree diffs. A line without the list reads as empty.</summary>
    public Candidate[] Candidates { get; init; } = Candidates ?? [];

    /// <summary>How the engine evaluates the submitted candidates.</summary>
    internal enum JudgeMode
    {
        /// <summary>Applies all candidates together onto one combined in-memory solution.</summary>
        Combine,

        /// <summary>Evaluates each candidate independently and ranks them on a scorecard.</summary>
        Race,
    }

    /// <summary>One candidate patch or worktree diff submitted to <c>fuse judge</c>.</summary>
    /// <param name="Name">Display label for the candidate (file name or worktree name).</param>
    /// <param name="Diff">Unified diff text relative to HEAD.</param>
    /// <param name="WorktreeRoot">Optional source worktree path when the candidate came from a worktree.</param>
    public sealed record Candidate(string Name, string Diff, string? WorktreeRoot = null)
    {
        /// <summary>Display label. A line without it reads as empty.</summary>
        public string Name { get; init; } = Name ?? "";

        /// <summary>Unified diff text. A line without it reads as empty.</summary>
        public string Diff { get; init; } = Diff ?? "";
    }
}
