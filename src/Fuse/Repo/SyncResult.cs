using System.Diagnostics;
using Fuse.Paths;

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
    private SyncResult(IReadOnlyCollection<RepoPath> paths) => Paths = paths;

    /// <summary>
    ///     The C# and Razor files whose content may differ from what the workspace last saw. After a reload the workspace
    ///     applies them again, because the loader reads each file as it was when its project opened.
    /// </summary>
    public IReadOnlyCollection<RepoPath> Paths { get; }

    /// <summary>
    ///     This result followed by <paramref name="later"/>, as one result that does the work of both when acted on once:
    ///     the case that does more wins (<see cref="Reevaluate"/>, then <see cref="Reload"/>, then <see cref="Patch"/>),
    ///     with the paths of both and, when both are patches, the vanished directories of both.
    /// </summary>
    /// <remarks>
    ///     The workspace uses it to finish a result it did not finish acting on, such as a re-evaluation after HEAD moved
    ///     that was cancelled: the tracker has already taken the new HEAD, so it will not report the move again. When
    ///     both are of the winning case, the later trigger is kept, because it names the latest reason.
    /// </remarks>
    public SyncResult Then(SyncResult later)
    {
        var paths = new HashSet<RepoPath>(Paths);
        paths.UnionWith(later.Paths);
        return (this, later) switch
        {
            (_, Reevaluate reevaluate) => new Reevaluate(paths, reevaluate.Trigger),
            (Reevaluate reevaluate, _) => new Reevaluate(paths, reevaluate.Trigger),
            (_, Reload reload) => new Reload(paths, reload.Trigger),
            (Reload reload, _) => new Reload(paths, reload.Trigger),
            (Patch earlier, Patch patch) => new Patch(paths, [.. earlier.VanishedDirectories.Union(patch.VanishedDirectories)]),
            _ => throw new UnreachableException($"a sync result is a re-evaluation, a reload or a patch, not {GetType().Name} and {later.GetType().Name}"),
        };
    }

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
    public sealed record Reevaluate(IReadOnlyCollection<RepoPath> Paths, string Trigger) : SyncResult(Paths);

    /// <summary>
    ///     More source files changed at once than patching one at a time is worth, and project configuration did not
    ///     change. The workspace closes every project and reopens the ones that were loaded, without evaluating them again.
    /// </summary>
    /// <param name="Paths">The files to apply again after the reload.</param>
    /// <param name="Trigger">How many changed files the sync collected, for the engine log.</param>
    public sealed record Reload(IReadOnlyCollection<RepoPath> Paths, string Trigger) : SyncResult(Paths);

    /// <summary>Only source files changed, and few enough to apply one at a time to both views.</summary>
    /// <param name="Paths">The files to apply to both views.</param>
    /// <param name="VanishedDirectories">
    ///     Directories that are missing from disk (deleted or renamed away). Every source the workspace holds under one is
    ///     gone, although no event named those files.
    /// </param>
    public sealed record Patch(IReadOnlyCollection<RepoPath> Paths, IReadOnlyCollection<RepoPath> VanishedDirectories) : SyncResult(Paths);
}
