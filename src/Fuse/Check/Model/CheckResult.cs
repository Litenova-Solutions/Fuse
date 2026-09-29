namespace Fuse.Check.Model;

/// <summary>What one check found: the errors the working tree introduced, and how far the check reached to find them.</summary>
/// <param name="Errors">Ordered by file and position, and capped, so a change that breaks a whole solution stays readable.</param>
/// <param name="FilesChecked">The targets and candidates bound, or every file of the projects bound whole.</param>
/// <param name="Projects">Names of the projects the errors are in, counted over every error found, not only the ones reported.</param>
/// <param name="DeclarationsChangedIn">
///     Names of the projects that own a target with a declaration change. Only these send the check on to their
///     dependents.
/// </param>
/// <param name="DependentProjectsChecked">How many dependents of those projects the check searched and bound.</param>
/// <param name="CheckedWholeProjects">True when there were too many candidates to bind one at a time, so whole projects were bound.</param>
internal sealed record CheckResult(
    IReadOnlyList<IntroducedError> Errors,
    int FilesChecked,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> DeclarationsChangedIn,
    int DependentProjectsChecked,
    bool CheckedWholeProjects);
