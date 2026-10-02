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
internal sealed record CheckReport(
    ReportedError[] Errors,
    int FilesChecked,
    string[] Projects,
    string[] DeclarationsChangedIn,
    int DependentProjectsChecked,
    bool CheckedWholeProjects,
    int ErrorCount,
    int ErrorFileCount);
