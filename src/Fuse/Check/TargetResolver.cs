using Fuse.Check.Model;
using Fuse.Paths;
using Fuse.Workspace;

namespace Fuse.Check;

/// <summary>
///     Turns a check's scope into its targets: the source files in it that an evaluated project owns, each once. A file
///     in build output or outside every project directory has no owner, so nothing would bind it and it is never a
///     target.
/// </summary>
internal sealed class TargetResolver
{
    private readonly RepoWorkspace _workspace;

    public TargetResolver(RepoWorkspace workspace) => _workspace = workspace;

    /// <summary>
    ///     Absolute paths of the files <paramref name="scope"/> names, which the sync reads from disk before the file
    ///     watcher reports them. Every change names none: the change tracker already has them.
    /// </summary>
    public IReadOnlyList<string> NamedPaths(CheckScope scope) =>
        scope is CheckScope.Files files ? [.. files.Paths.Select(_workspace.Root.Absolute)] : [];

    /// <summary>The targets of <paramref name="scope"/>, read after the sync so every change includes the latest edits.</summary>
    public IReadOnlyList<string> Resolve(CheckScope scope)
    {
        var graph = _workspace.Graph;
        var paths = scope is CheckScope.Files ? NamedPaths(scope) : _workspace.Tracker.Changed;
        return paths
            .Where(PathRules.IsSource)
            .Distinct(PathRules.PathComparer)
            .Where(p => graph.OwnersOf(p).Count > 0)
            .ToList();
    }
}
