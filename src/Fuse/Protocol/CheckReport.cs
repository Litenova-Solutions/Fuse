namespace Fuse.Protocol;

/// <summary>The errors introduced by the working-tree changes, and how far the check reached, as the client receives them.</summary>
/// <param name="Errors">Working-tree errors absent at HEAD, ordered by file and position, each with the cause to print under it.</param>
/// <param name="FilesChecked">Number of target and candidate files the check examined.</param>
/// <param name="Projects">Names of the projects the introduced errors are in.</param>
/// <param name="DeclarationsChangedIn">Projects whose declarations changed, which is what triggers checking dependents.</param>
/// <param name="DependentProjectsChecked">Number of dependent projects searched for breaks.</param>
/// <param name="CheckedWholeProjects">True when the candidate count exceeded the threshold and whole projects were checked.</param>
/// <param name="CausesLeftOut">How many errors had a cause that the cap on causes per answer left out.</param>
internal sealed record CheckReport(
    ReportedError[] Errors,
    int FilesChecked,
    string[] Projects,
    string[] DeclarationsChangedIn,
    int DependentProjectsChecked,
    bool CheckedWholeProjects,
    int CausesLeftOut);
