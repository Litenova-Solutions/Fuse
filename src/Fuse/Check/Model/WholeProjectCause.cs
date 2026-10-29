namespace Fuse.Check.Model;

/// <summary>
///     Why a project was bound whole: the changed files that forced it. Reporting only; what was bound does not change.
/// </summary>
/// <param name="Project">The name of the project that was bound whole.</param>
/// <param name="ChangedFiles">Repository-relative paths of the changed files, distinct and in path order.</param>
/// <param name="Causes">
///     The declaration changes that forced it, each pre-rendered as <c>removed: ...</c> or <c>changed: ...</c>, distinct and
///     in path then cause order.
/// </param>
internal sealed record WholeProjectCause(string Project, IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> Causes);
