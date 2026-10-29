namespace Fuse.Check.Model;

/// <summary>What one check found: the errors the working-tree changes introduced, and how far the check reached to find them.</summary>
/// <param name="Errors">Ordered by file and position, and capped, so a change that breaks a whole solution stays readable.</param>
/// <param name="FilesChecked">The targets and candidates bound, or every file of the projects bound whole.</param>
/// <param name="Projects">Names of the projects the errors are in, counted over every error found, not only the ones reported.</param>
/// <param name="DeclarationsChangedIn">
///     Names of the projects that own a target with a declaration change. Only these send the check on to their
///     dependents.
/// </param>
/// <param name="DependentProjectsChecked">How many dependents of those projects the check searched and bound.</param>
/// <param name="CheckedWholeProjects">True when there were too many candidates to bind one at a time, so whole projects were bound.</param>
/// <param name="ErrorCount">How many errors were found, counted before the cap on <paramref name="Errors"/>.</param>
/// <param name="ErrorFileCount">How many files those errors are in, counted before the cap.</param>
/// <param name="LeftToOtherSessions">
///     How many errors the check found that other sessions' edits caused without the session the check is answered to,
///     and that are left out of <paramref name="Errors"/> and the counts above. Always 0 for a check answered to no session.
/// </param>
internal sealed record CheckResult(
    IReadOnlyList<IntroducedError> Errors,
    int FilesChecked,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> DeclarationsChangedIn,
    int DependentProjectsChecked,
    bool CheckedWholeProjects,
    int ErrorCount,
    int ErrorFileCount,
    int LeftToOtherSessions)
{
    /// <summary>
    ///     How many files or whole projects the check answered without all their analyzer errors, because the analyzers
    ///     had not finished when the client's wait was almost over. Their compiler errors are complete.
    /// </summary>
    public int AnalyzersUnfinished { get; init; }

    /// <summary>The compilation-wide analyzers the check left out, by assembly and type name; the stop hook runs them.</summary>
    public IReadOnlyList<string> CompilationWideLeftOut { get; init; } = [];

    /// <summary>
    ///     How many files with errors the session was told about without the check deciding whose edits caused them,
    ///     because more files needed deciding than attribution decides in one check.
    /// </summary>
    public int FilesNotAttributed { get; init; }

    /// <summary>How many changed files are not C# sources, which the check does not read.</summary>
    public int NonCSharpChanged { get; init; }

    /// <summary>The first changed non-C# files by sorted relative path, so the answer can name them.</summary>
    public IReadOnlyList<string> NonCSharpSample { get; init; } = [];

    /// <summary>
    ///     The paths a scoped check was scoped to, repository-relative with forward slashes, in the order named. Empty for
    ///     a check of every change or of named files.
    /// </summary>
    public IReadOnlyList<string> ScopedPaths { get; init; } = [];

    /// <summary>
    ///     Why each project was bound whole, in project-name order. Empty when the check bound no project whole.
    /// </summary>
    public IReadOnlyList<WholeProjectCause> WholeProjectCauses { get; init; } = [];
}
