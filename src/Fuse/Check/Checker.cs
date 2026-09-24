using Fuse.Graph;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Fuse.Protocol.Diagnostic;

namespace Fuse.Check;

/// <summary>
///     Reports the errors the working tree has that HEAD did not, in the changed files and in every file a
///     declaration change can reach, including files in dependent projects.
/// </summary>
/// <remarks>
///     The scope grows only as far as the change requires:
///     <list type="number">
///         <item>Every target file is bound in the working tree and at HEAD, and the error sets are diffed.</item>
///         <item>
///             If a target's declarations are unchanged (a body-only edit), nothing else can gain an error and the check
///             ends there.
///         </item>
///         <item>
///             Otherwise the owning projects and their dependents are loaded. A name-bound change re-checks only files
///             that mention one of the changed names; a broad change (base list, operator, global using) re-checks
///             every file. Past <see cref="WholeProjectThreshold"/> candidate files, whole projects are bound instead.
///         </item>
///     </list>
/// </remarks>
internal sealed class Checker
{
    private const int WholeProjectThreshold = 500;
    private const int MaxReported = 200;

    private readonly RepoWorkspace _workspace;
    private readonly DiagnosticCollector _collector;

    public Checker(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _collector = new DiagnosticCollector(workspace.Root);
    }

    /// <summary>Runs a check.</summary>
    /// <param name="files">Files to scope to (the ones just edited), or null for every change since HEAD.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<CheckReport> CheckAsync(IReadOnlyCollection<string>? files, CancellationToken cancellationToken)
    {
        var root = _workspace.Root;
        var known = files?.Select(root.Absolute).ToList() ?? [];
        await _workspace.SyncAsync(known, cancellationToken).ConfigureAwait(false);

        var graph = _workspace.Graph;
        var targets = (files is null ? _workspace.Tracker.Changed : known)
            .Where(ChangeTracker.IsSource)
            .Distinct(ChangeTracker.PathComparer)
            .Where(p => graph.OwnersOf(p).Count > 0)
            .ToList();
        if (targets.Count == 0)
            return new CheckReport([], 0, [], [], 0, false);

        var owners = targets.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        await _workspace.EnsureLoadedAsync(owners, cancellationToken).ConfigureAwait(false);

        var introduced = new List<Diagnostic>();
        foreach (var path in targets)
            introduced.AddRange(await IntroducedInFileAsync(path, cancellationToken).ConfigureAwait(false));
        var filesChecked = targets.Count;

        // Which declarations changed, and which owning projects they live in.
        var broad = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var surfaceProjects = new List<ProjectNode>();
        foreach (var path in targets)
        {
            var (fileBroad, fileNames) = await SurfaceChangeAsync(path, cancellationToken).ConfigureAwait(false);
            if (!fileBroad && fileNames.Count == 0)
                continue;
            broad |= fileBroad;
            names.UnionWith(fileNames);
            surfaceProjects.AddRange(graph.OwnersOf(path));
        }

        surfaceProjects = surfaceProjects.DistinctBy(p => p.Path).ToList();
        var dependents = new List<ProjectNode>();
        var wholeProjects = false;
        if (surfaceProjects.Count > 0)
        {
            dependents = surfaceProjects.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
                .Where(p => !surfaceProjects.Any(s => ChangeTracker.PathComparer.Equals(s.Path, p.Path)))
                .ToList();
            await _workspace.EnsureLoadedAsync(dependents, cancellationToken).ConfigureAwait(false);

            var reach = surfaceProjects.Concat(dependents).ToList();
            var targetSet = new HashSet<string>(targets, ChangeTracker.PathComparer);
            var candidates = await CandidatesAsync(reach, targetSet, broad ? null : names, cancellationToken).ConfigureAwait(false);
            if (candidates.Count > WholeProjectThreshold)
            {
                wholeProjects = true;
                foreach (var node in reach)
                    introduced.AddRange(await IntroducedInProjectAsync(node, cancellationToken).ConfigureAwait(false));
                filesChecked = _workspace.Current.Projects.Where(p => reach.Any(r => ChangeTracker.PathComparer.Equals(r.Path, p.FilePath))).Sum(p => p.DocumentIds.Count);
            }
            else
            {
                foreach (var path in candidates)
                    introduced.AddRange(await IntroducedInFileAsync(path, cancellationToken).ConfigureAwait(false));
                filesChecked += candidates.Count;
            }
        }

        var ordered = introduced.Distinct()
            .OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column)
            .ToList();
        var projects = ordered
            .Select(d => graph.OwnersOf(root.Absolute(d.Path)) is [var owner, ..] ? owner.Name : null)
            .OfType<string>()
            .Distinct()
            .ToArray();
        return new CheckReport(
            [.. ordered.Take(MaxReported)],
            filesChecked,
            projects,
            [.. surfaceProjects.Select(p => p.Name)],
            dependents.Count,
            wholeProjects);
    }

    private async Task<IEnumerable<Diagnostic>> IntroducedInFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];
        var current = await _collector.ForFileAsync(_workspace.Current, path, cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
            return [];
        var baseline = await _collector.ForBaselineFileAsync(_workspace.Baseline, _workspace.BaselineGeneration, path, cancellationToken).ConfigureAwait(false);
        return DiagnosticDelta.Introduced(current, baseline).ToList();
    }

    private async Task<IEnumerable<Diagnostic>> IntroducedInProjectAsync(ProjectNode node, CancellationToken cancellationToken)
    {
        var result = new List<Diagnostic>();
        foreach (var project in RepoWorkspace.ProjectsFor(_workspace.Current, node))
        {
            var current = await _collector.ForProjectAsync(project, cancellationToken).ConfigureAwait(false);
            if (current.Count == 0)
                continue;
            var baselineProject = _workspace.Baseline.GetProject(project.Id);
            var baseline = baselineProject is null
                ? []
                : await _collector.ForBaselineProjectAsync(baselineProject, _workspace.BaselineGeneration, cancellationToken).ConfigureAwait(false);
            result.AddRange(DiagnosticDelta.Introduced(current, baseline));
        }

        return result;
    }

    /// <summary>Compares the file's declarations at HEAD and now.</summary>
    private async Task<(bool Broad, HashSet<string> Names)> SurfaceChangeAsync(string path, CancellationToken cancellationToken)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // A Razor component is used by its file name; a change to its parameters reaches files that name it.
            var component = Path.GetFileNameWithoutExtension(path);
            var razorHead = _workspace.HeadText(path);
            var razorNow = File.Exists(path) ? RepoWorkspace.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)) : null;
            return razorHead is not null && razorNow is not null && razorHead.ContentEquals(razorNow)
                ? (false, [])
                : (false, [component]);
        }

        var head = _workspace.HeadText(path);
        SourceText? now = null;
        if (File.Exists(path))
        {
            var documentId = _workspace.Current.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            now = documentId is not null && _workspace.Current.GetDocument(documentId) is { } document
                ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
                : RepoWorkspace.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }

        if (head is not null && now is not null && head.ContentEquals(now))
            return (false, []);
        var before = head is null ? [] : SurfaceMap.Compute(await ParseAsync(head, cancellationToken).ConfigureAwait(false));
        var after = now is null ? [] : SurfaceMap.Compute(await ParseAsync(now, cancellationToken).ConfigureAwait(false));
        return SurfaceMap.Diff(before, after);
    }

    private static Task<SyntaxNode> ParseAsync(SourceText text, CancellationToken cancellationToken) =>
        CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), cancellationToken: cancellationToken)
            .GetRootAsync(cancellationToken);

    /// <summary>Files in <paramref name="reach"/> that could observe the change: every file for a broad change, or files mentioning a changed name.</summary>
    private async Task<List<string>> CandidatesAsync(IReadOnlyList<ProjectNode> reach, HashSet<string> exclude, HashSet<string>? names, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(ChangeTracker.PathComparer);
        foreach (var node in reach)
        {
            foreach (var project in RepoWorkspace.ProjectsFor(_workspace.Current, node))
            {
                foreach (var document in project.Documents)
                {
                    var path = document.FilePath;
                    if (path is null || exclude.Contains(path) || !seen.Add(path))
                        continue;
                    if (names is not null)
                    {
                        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        var content = text.ToString();
                        if (!names.Any(n => content.Contains(n, StringComparison.Ordinal)))
                            continue;
                    }

                    result.Add(path);
                    if (result.Count > WholeProjectThreshold)
                        return result;
                }

                foreach (var additional in project.AdditionalDocuments)
                {
                    var path = additional.FilePath;
                    if (path is null || !ChangeTracker.IsSource(path) || exclude.Contains(path) || !seen.Add(path))
                        continue;
                    if (names is not null)
                    {
                        var content = (await additional.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                        if (!names.Any(n => content.Contains(n, StringComparison.Ordinal)))
                            continue;
                    }

                    result.Add(path);
                }
            }
        }

        return result;
    }
}
