namespace Fuse.Repo;

/// <summary>
///     How the workspace follows the changes one <see cref="ChangeTracker.SyncAsync"/> found: it evaluates the projects
///     again and reloads them, reloads them, or patches the changed files into the loaded projects.
/// </summary>
/// <remarks>
///     There is one case per response, so a combination that never happens cannot be written. HEAD moving always
///     re-evaluates, because the commit it moved to can have other project files, so no case says whether HEAD moved; a
///     reload that has to evaluate the projects again is a <see cref="Reevaluate"/>.
/// </remarks>
internal abstract record SyncResult
{
    private SyncResult(IReadOnlyCollection<string> paths) => Paths = paths;

    /// <summary>
    ///     Absolute paths of C# and Razor files whose content may differ from what the workspace last saw. After a
    ///     reload the workspace applies them again, because the loader reads each file as it was when its project opened.
    /// </summary>
    public IReadOnlyCollection<string> Paths { get; }

    /// <summary>
    ///     Project configuration may have changed: HEAD moved, a project, props, targets, editorconfig or global.json file
    ///     changed, or the file watcher lost events, one of which could have been such a file. The workspace evaluates
    ///     every project again, closes them all, and reopens the ones that were loaded.
    /// </summary>
    /// <param name="Paths">The files to apply again after the reload.</param>
    /// <param name="Trigger">
    ///     Why the projects are evaluated again, for the engine log: the commit HEAD moved to (or that HEAD has none), the
    ///     watcher's error, or the project file whose event arrived last.
    /// </param>
    public sealed record Reevaluate(IReadOnlyCollection<string> Paths, string Trigger) : SyncResult(Paths);

    /// <summary>
    ///     More source files changed at once than patching one at a time is worth, and project configuration did not
    ///     change. The workspace closes every project and reopens the ones that were loaded, without evaluating them again.
    /// </summary>
    /// <param name="Paths">The files to apply again after the reload.</param>
    /// <param name="Trigger">How many changed files the sync collected, for the engine log.</param>
    public sealed record Reload(IReadOnlyCollection<string> Paths, string Trigger) : SyncResult(Paths);

    /// <summary>Only source files changed, and few enough to apply one at a time to both views.</summary>
    /// <param name="Paths">The files to apply to both views.</param>
    /// <param name="VanishedDirectories">
    ///     Directories that are missing from disk (deleted or renamed away). Every source the workspace holds under one is
    ///     gone, although no event named those files.
    /// </param>
    public sealed record Patch(IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> VanishedDirectories) : SyncResult(Paths);
}
