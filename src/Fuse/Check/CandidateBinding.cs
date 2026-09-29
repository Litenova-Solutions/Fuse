using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;

namespace Fuse.Check;

/// <summary>
///     Binds the candidates a reach names and keeps their introduced errors. Past <see cref="WholeProjectThreshold"/>
///     candidates it binds every reached project whole instead, the targets' owners included.
/// </summary>
internal sealed class CandidateBinding
{
    private const int WholeProjectThreshold = 500;

    private readonly RepoWorkspace _workspace;
    private readonly IntroducedErrors _introduced;

    public CandidateBinding(RepoWorkspace workspace, IntroducedErrors introduced)
    {
        _workspace = workspace;
        _introduced = introduced;
    }

    /// <summary>Binds what <paramref name="reach"/> names, leaving out the targets, which are already bound.</summary>
    /// <param name="reach">The files the declaration changes can break; <see cref="Reach.None"/> binds nothing.</param>
    /// <param name="reached">The projects with declaration changes and their dependents, which are bound whole past the threshold.</param>
    /// <param name="targets">The targets, which the check has bound already.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    /// <returns>
    ///     The errors introduced in the candidates, how many source files the whole check bound (the targets included),
    ///     and whether whole projects were bound. A file counts once however many target frameworks compile it, and only
    ///     when a project owns it, so the files the build generates under <c>bin</c> and <c>obj</c> are bound but not
    ///     counted.
    /// </returns>
    public async Task<(List<CompilerError> Errors, int FilesChecked, bool CheckedWholeProjects)> BindAsync(
        Reach reach, IReadOnlyList<ProjectNode> reached, IReadOnlyCollection<RepoPath> targets, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<RepoPath>(
            reach switch
            {
                Reach.Broad broad => broad.Files,
                Reach.Precise precise => precise.Causes.Keys,
                _ => [],
            });
        candidates.ExceptWith(targets);

        var graph = _workspace.Graph;
        if (candidates.Count <= WholeProjectThreshold)
            return (await _introduced.InFilesAsync(candidates, cancellationToken).ConfigureAwait(false), targets.Count + candidates.Count(c => graph.OwnersOf(c).Count > 0), false);

        var errors = new List<CompilerError>();
        foreach (var node in reached)
            errors.AddRange(await _introduced.InProjectAsync(node, cancellationToken).ConfigureAwait(false));
        // The targets outside the reached projects were bound one by one, before this step.
        var files = new HashSet<RepoPath>(targets);
        foreach (var path in reached.SelectMany(n => RepoWorkspace.ProjectsFor(_workspace.Current, n)).SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>())
        {
            var file = _workspace.Root.PathOf(path);
            if (graph.OwnersOf(file).Count > 0)
                files.Add(file);
        }

        return (errors, files.Count, true);
    }
}
