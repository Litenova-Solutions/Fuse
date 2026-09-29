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
    /// <param name="targets">Absolute paths of the targets.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    /// <returns>
    ///     The errors introduced in the candidates, how many files the whole check bound (the targets included), and
    ///     whether whole projects were bound.
    /// </returns>
    public async Task<(List<CompilerError> Errors, int FilesChecked, bool CheckedWholeProjects)> BindAsync(
        Reach reach, IReadOnlyList<ProjectNode> reached, IReadOnlyCollection<string> targets, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(
            reach switch
            {
                Reach.Broad broad => broad.Files,
                Reach.Precise precise => precise.Causes.Keys,
                _ => [],
            },
            PathRules.PathComparer);
        candidates.ExceptWith(targets);

        if (candidates.Count <= WholeProjectThreshold)
            return (await _introduced.InFilesAsync(candidates, cancellationToken).ConfigureAwait(false), targets.Count + candidates.Count, false);

        var errors = new List<CompilerError>();
        foreach (var node in reached)
            errors.AddRange(await _introduced.InProjectAsync(node, cancellationToken).ConfigureAwait(false));
        var files = reached.SelectMany(n => RepoWorkspace.ProjectsFor(_workspace.Current, n)).Sum(p => p.DocumentIds.Count);
        return (errors, files, true);
    }
}
