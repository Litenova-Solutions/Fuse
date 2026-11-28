namespace Fuse.Judge.Model;

/// <summary>One candidate's score: whether it applies and compiles, how many errors it introduces, and how long it took.</summary>
/// <param name="Name">The name the candidate was scored under.</param>
/// <param name="Rank">One-based rank, or the candidate's order for a combined run.</param>
/// <param name="Compiles">True when the candidate introduces no error.</param>
/// <param name="ErrorCount">How many errors the candidate introduces.</param>
/// <param name="AnalyzerErrorCount">How many of the introduced errors an analyzer reported.</param>
/// <param name="SurfaceBreaks">How many declaration changes the candidate's fork holds.</param>
/// <param name="ElapsedMs">How long scoring the candidate took, in milliseconds.</param>
/// <param name="Errors">The errors the candidate introduces.</param>
/// <param name="ConflictReason">Why the patch does not apply, or null when it does.</param>
internal sealed record CandidateScore(
    string Name,
    int Rank,
    bool Compiles,
    int ErrorCount,
    int AnalyzerErrorCount = 0,
    int SurfaceBreaks = 0,
    long ElapsedMs = 0,
    IReadOnlyList<CandidateScore.ScoredError>? Errors = null,
    string? ConflictReason = null)
{
    /// <summary>The errors the candidate introduces.</summary>
    public IReadOnlyList<ScoredError> Errors { get; init; } = Errors ?? [];

    /// <summary>One error the candidate introduces, with the declaration change that reached its file.</summary>
    /// <param name="Path">Repository-relative path with forward slashes.</param>
    /// <param name="Line">One-based line.</param>
    /// <param name="Column">One-based column.</param>
    /// <param name="Id">The diagnostic id, for example <c>CS1061</c>.</param>
    /// <param name="Message">The message, in the invariant culture.</param>
    /// <param name="FromAnalyzer">True when an analyzer reported it.</param>
    /// <param name="Cause">The declaration change that reached the error's file, or null when it prints with none.</param>
    /// <param name="IsCauseLeftOut">True when the error had a cause that the printed answer leaves out.</param>
    public sealed record ScoredError(
        string Path,
        int Line,
        int Column,
        string Id,
        string Message,
        bool FromAnalyzer,
        ScoredCause? Cause = null,
        bool IsCauseLeftOut = false)
    {
        /// <summary>The repository-relative path of the generated file that declares what the error is about, or null.</summary>
        public string? GeneratedFile { get; init; }

        /// <summary>The session that wrote the file holding the cause's declaration, or null.</summary>
        public string? CausedBy { get; init; }
    }

    /// <summary>The declaration change that reached the error's file.</summary>
    /// <param name="Removed">True when HEAD has the declaration and the working tree does not.</param>
    /// <param name="Declaration">The declaration's header on one line.</param>
    public sealed record ScoredCause(bool Removed, string Declaration);
}
