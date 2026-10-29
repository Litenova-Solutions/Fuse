namespace Fuse.Protocol;

/// <summary>The errors introduced by the working-tree changes, and how far the check reached, as the client receives them.</summary>
/// <param name="Errors">Working-tree errors absent at HEAD, ordered by file and position, each with the cause to print under it.</param>
/// <param name="FilesChecked">Number of target and candidate files the check examined.</param>
/// <param name="Projects">Names of the projects the introduced errors are in.</param>
/// <param name="DeclarationsChangedIn">Projects whose declarations changed, which is what triggers checking dependents.</param>
/// <param name="DependentProjectsChecked">Number of dependent projects searched for breaks.</param>
/// <param name="CheckedWholeProjects">True when the candidate count exceeded the threshold and whole projects were checked.</param>
/// <param name="ErrorCount">How many errors the check found, which is more than <paramref name="Errors"/> holds when they were capped.</param>
/// <param name="ErrorFileCount">How many files those errors are in.</param>
/// <param name="LeftToOtherSessions">How many errors other sessions' edits caused, left out of the errors and counts above.</param>
internal sealed record CheckReport(
    ReportedError[] Errors,
    int FilesChecked,
    string[] Projects,
    string[] DeclarationsChangedIn,
    int DependentProjectsChecked,
    bool CheckedWholeProjects,
    int ErrorCount,
    int ErrorFileCount,
    int LeftToOtherSessions)
{
    /// <summary>How many files or whole projects were answered before their analyzers finished; their compiler errors are complete.</summary>
    public int AnalyzersUnfinished { get; init; }

    /// <summary>The compilation-wide analyzers the check left out, by assembly and type name.</summary>
    public string[] CompilationWideLeftOut { get; init; } = [];

    /// <summary>How many files with errors were told to the session without deciding whose edits caused them.</summary>
    public int FilesNotAttributed { get; init; }

    /// <summary>The paths a scoped check was scoped to, repository-relative with forward slashes, in the order named.</summary>
    public string[] ScopedPaths { get; init; } = [];

    /// <summary>Why each project was bound whole, in project-name order.</summary>
    public ReportedWholeProject[] WholeProjects { get; init; } = [];
}
