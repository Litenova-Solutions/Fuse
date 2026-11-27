using System.Collections.Concurrent;
using System.Text;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Check.Candidates;
using Fuse.Check.Diagnostics;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Judge;

/// <summary>
///     Forks the current solution with one candidate's patch and checks the fork for the errors it introduces compared
///     with the baseline, with the checker's own steps. It reads the fork's own texts and binds from the pinned
///     generation the fork was derived from.
/// </summary>
internal static class ForkChecker
{
    /// <summary>Checks one fork for the errors it introduces compared with the baseline, with the checker's own steps.</summary>
    /// <returns>The introduced errors, the reach they were found with, and how many declaration changes the fork holds.</returns>
    internal static async Task<(List<CompilerError> Errors, Reach Reach, int Breaks)> CheckForkAsync(
        Solution fork,
        Generation generation,
        IReadOnlyList<RepoPath> targets,
        IntroducedErrors introduced,
        ChangeReach reacher,
        CandidateBinding binding,
        CancellationToken cancellationToken)
    {
        var forkGeneration = generation with { Current = fork };
        var context = new CheckContext(AnalyzerRun.Full, reuse: false);
        introduced.BeginContext(context);
        var errors = new ConcurrentBag<CompilerError>();
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (target, ct) =>
            {
                foreach (var error in await introduced.InFileOfAsync(fork, target, forkGeneration, context, ct).ConfigureAwait(false))
                    errors.Add(error);
            }).ConfigureAwait(false);

        var (changed, breaks) = await DeclarationChangesAsync(targets, generation.Baseline, fork, cancellationToken).ConfigureAwait(false);
        Reach reach = new Reach.None();
        if (changed.Count > 0)
        {
            var graph = forkGeneration.Graph;
            var changedIn = changed.SelectMany(graph.OwnersOf).DistinctBy(n => n.Path).ToList();
            var dependents = changedIn.SelectMany(graph.DependentsOf).DistinctBy(n => n.Path).Where(n => !changedIn.Any(o => o.Path == n.Path)).ToList();
            var reached = changedIn.Concat(dependents).ToList();
            reach = await reacher.ReachAsync(changed, reached, forkGeneration, cancellationToken).ConfigureAwait(false);
            var bound = await binding.BindAsync(reach, reached, targets, forkGeneration, context.WithReuse(), cancellationToken).ConfigureAwait(false);
            foreach (var error in bound.Errors)
                errors.Add(error);
        }

        return ([.. errors], reach, breaks);
    }

    /// <summary>Each touched file's text in the current view, or in the baseline when the current view does not hold it.</summary>
    internal static async Task<Dictionary<RepoPath, string?>> ReadBasesAsync(Generation generation, IReadOnlyList<RepoPath> touched, CancellationToken cancellationToken)
    {
        var bases = new Dictionary<RepoPath, string?>();
        foreach (var path in touched)
        {
            bases[path] = await DocumentTextAsync(generation.Current, path, cancellationToken).ConfigureAwait(false)
                ?? await DocumentTextAsync(generation.Baseline, path, cancellationToken).ConfigureAwait(false);
        }

        return bases;
    }

    /// <summary>The current view with the buffers' texts, and the files that differ from it.</summary>
    internal static async Task<(Solution Fork, List<RepoPath> Touched)> ForkAsync(Solution current, RepoGraph graph, IReadOnlyDictionary<RepoPath, string?> buffers, CancellationToken cancellationToken)
    {
        var fork = current;
        var touched = new List<RepoPath>();
        foreach (var (path, text) in buffers.OrderBy(b => b.Key.Absolute, StringComparer.Ordinal))
        {
            if (await DocumentTextAsync(fork, path, cancellationToken).ConfigureAwait(false) == text)
                continue;
            fork = await WithForkContentAsync(fork, graph, path, text is null ? null : SourceText.From(text, Encoding.UTF8, SourceHashAlgorithm.Sha256), cancellationToken).ConfigureAwait(false);
            touched.Add(path);
        }

        return (fork, touched);
    }

    /// <summary>Makes every document for the path hold the text, adding or removing documents as needed.</summary>
    private static async Task<Solution> WithForkContentAsync(Solution fork, RepoGraph graph, RepoPath path, SourceText? text, CancellationToken cancellationToken)
    {
        var ids = fork.GetDocumentIdsWithFilePath(path.Absolute);
        if (text is null)
        {
            foreach (var id in ids)
            {
                fork = fork.GetDocument(id) is not null ? fork.RemoveDocument(id)
                    : fork.GetAdditionalDocument(id) is not null ? fork.RemoveAdditionalDocument(id)
                    : fork;
            }

            return fork;
        }

        if (!ids.IsEmpty)
        {
            foreach (var id in ids)
            {
                TextDocument? existing = fork.GetDocument(id) ?? fork.GetAdditionalDocument(id);
                if (existing is null)
                    continue;
                if ((await existing.GetTextAsync(cancellationToken).ConfigureAwait(false)).ContentEquals(text))
                    continue;
                fork = fork.GetDocument(id) is not null
                    ? fork.WithDocumentText(id, text, PreservationMode.PreserveIdentity)
                    : fork.WithAdditionalDocumentText(id, text, PreservationMode.PreserveIdentity);
            }

            return fork;
        }

        foreach (var owner in graph.OwnersOf(path))
        {
            foreach (var project in RepoWorkspace.ProjectsFor(fork, owner).ToList())
            {
                var id = DocumentId.CreateNewId(project.Id, path.Absolute);
                var folders = Path.GetRelativePath(owner.Directory.Absolute, Path.GetDirectoryName(path.Absolute)!)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != ".")
                    .ToArray();
                fork = path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? fork.AddDocument(id, path.FileName, text, folders, path.Absolute)
                    : fork.AddAdditionalDocument(id, path.FileName, text, folders, path.Absolute);
            }
        }

        return fork;
    }

    /// <summary>The text of the file's document in the view, or null when the view holds no document for it.</summary>
    private static async Task<string?> DocumentTextAsync(Solution view, RepoPath path, CancellationToken cancellationToken)
    {
        var id = view.GetDocumentIdsWithFilePath(path.Absolute).FirstOrDefault();
        TextDocument? document = id is null ? null : view.GetDocument(id) ?? (TextDocument?)view.GetAdditionalDocument(id);
        return document is null ? null : (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
    }

    /// <summary>The touched files whose declarations differ between the baseline and the fork, and how many changes they hold.</summary>
    /// <remarks>It reads the same comparison the check reads, from the fork's own texts, because the checker's freshness read would see disk.</remarks>
    private static async Task<(List<RepoPath> Changed, int Breaks)> DeclarationChangesAsync(IReadOnlyList<RepoPath> touched, Solution baseline, Solution fork, CancellationToken cancellationToken)
    {
        var changed = new List<RepoPath>();
        var breaks = 0;
        foreach (var path in touched.OrderBy(p => p.Absolute, StringComparer.Ordinal))
        {
            if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (await DocumentTextAsync(baseline, path, cancellationToken).ConfigureAwait(false) != await DocumentTextAsync(fork, path, cancellationToken).ConfigureAwait(false))
                {
                    changed.Add(path);
                    breaks++;
                }

                continue;
            }

            var seen = new HashSet<(DeclarationKey Key, string Kind, string Before, string After)>();
            var fileBreaks = 0;
            var projectIds = baseline.GetDocumentIdsWithFilePath(path.Absolute).Concat(fork.GetDocumentIdsWithFilePath(path.Absolute)).Select(id => id.ProjectId).Distinct();
            foreach (var projectId in projectIds)
            {
                var before = await DeclarationsOfAsync(DocumentIn(baseline, projectId, path), cancellationToken).ConfigureAwait(false);
                var after = await DeclarationsOfAsync(DocumentIn(fork, projectId, path), cancellationToken).ConfigureAwait(false);
                foreach (var change in SurfaceDiff.Compare(before, after).Changes)
                {
                    if (change is DeclarationChange.Added && change.Key is DeclarationKey.Using)
                        continue;
                    var kind = change switch
                    {
                        DeclarationChange.Added => "added",
                        DeclarationChange.Removed => "removed",
                        _ => "changed",
                    };
                    var (beforeHeader, afterHeader) = change switch
                    {
                        DeclarationChange.Added added => ("", added.After),
                        DeclarationChange.Removed removed => (removed.Before, ""),
                        DeclarationChange.Changed edited => (edited.Before, edited.After),
                        _ => ("", ""),
                    };
                    if (seen.Add((change.Key, kind, beforeHeader, afterHeader)))
                        fileBreaks++;
                }
            }

            if (fileBreaks > 0)
            {
                changed.Add(path);
                breaks += fileBreaks;
            }
        }

        return (changed, breaks);
    }

    /// <summary>The document for the path in the project's view of the solution, or null.</summary>
    private static Document? DocumentIn(Solution solution, ProjectId projectId, RepoPath path) =>
        solution.GetDocumentIdsWithFilePath(path.Absolute).Where(id => id.ProjectId == projectId).Select(solution.GetDocument).FirstOrDefault(d => d is not null);

    /// <summary>The declarations of the document's syntax root, or none when there is no document.</summary>
    private static async Task<FileDeclarations> DeclarationsOfAsync(Document? document, CancellationToken cancellationToken) =>
        document is not null && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root ? FileDeclarations.Of(root) : FileDeclarations.Empty;
}
