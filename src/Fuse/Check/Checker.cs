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
///     Reports the errors the working tree has that HEAD does not, in the changed files and in every file a
///     declaration change can reach, including files in dependent projects.
/// </summary>
/// <remarks>
///     The scope grows only as far as the change requires:
///     <list type="number">
///         <item>Every existing target file is bound in the working tree; when it has errors, they are diffed with the same file at HEAD.</item>
///         <item>
///             If a target's declarations are unchanged (a body-only edit), nothing else can gain an error and the check
///             ends there.
///         </item>
///         <item>
///             Otherwise the owning projects and their dependents are loaded and <see cref="ChangeReach"/> finds the
///             files that use the changed declarations; a broad change (type header, delegate, global using, assembly
///             attribute) re-checks every file. Past <see cref="WholeProjectThreshold"/> candidate files, whole
///             projects are bound instead.
///         </item>
///     </list>
/// </remarks>
internal sealed class Checker
{
    private const int WholeProjectThreshold = 500;
    private const int MaxReported = 200;

    private readonly RepoWorkspace _workspace;
    private readonly DiagnosticCollector _collector;
    private readonly ChangeReach _reach;

    public Checker(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _collector = new DiagnosticCollector(workspace.Root, () => workspace.LoaderGeneration);
        _reach = new ChangeReach(workspace);
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
        var timer = System.Diagnostics.Stopwatch.StartNew();
        introduced.AddRange(await IntroducedInFilesAsync(targets, cancellationToken).ConfigureAwait(false));
        var targetsMs = timer.ElapsedMilliseconds;
        var filesChecked = targets.Count;

        // Which owning projects have declaration changes (syntax only), then what those changes can reach.
        var surfaceTargets = new List<string>();
        foreach (var path in targets)
        {
            if (await _reach.HasSurfaceChangeAsync(path, cancellationToken).ConfigureAwait(false))
                surfaceTargets.Add(path);
        }

        var surfaceProjects = surfaceTargets.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        var dependents = new List<ProjectNode>();
        var wholeProjects = false;
        if (surfaceProjects.Count > 0)
        {
            dependents = surfaceProjects.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
                .Where(p => !surfaceProjects.Any(s => ChangeTracker.PathComparer.Equals(s.Path, p.Path)))
                .ToList();
            await _workspace.EnsureLoadedAsync(dependents, cancellationToken).ConfigureAwait(false);

            var reachNodes = surfaceProjects.Concat(dependents).ToList();
            var reach = reachNodes.SelectMany(n => RepoWorkspace.ProjectsFor(_workspace.Current, n)).ToList();
            var targetSet = new HashSet<string>(targets, ChangeTracker.PathComparer);
            var reachedFiles = await _reach.FilesAsync(surfaceTargets, reach, cancellationToken).ConfigureAwait(false);
            var candidates = reachedFiles is null
                ? reach.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>().Distinct(ChangeTracker.PathComparer).ToList()
                : reachedFiles.ToList();
            candidates.RemoveAll(targetSet.Contains);
            if (candidates.Count > WholeProjectThreshold)
            {
                wholeProjects = true;
                foreach (var node in reachNodes)
                    introduced.AddRange(await IntroducedInProjectAsync(node, cancellationToken).ConfigureAwait(false));
                filesChecked = reach.Sum(p => p.DocumentIds.Count);
            }
            else
            {
                introduced.AddRange(await IntroducedInFilesAsync(candidates, cancellationToken).ConfigureAwait(false));
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
        var (compilerMs, analyzerMs) = _collector.TakeTimings();
        _workspace.Log($"check: binding {compilerMs} ms, analyzers {analyzerMs} ms (summed over files); {targets.Count} target(s) in {targetsMs} ms, {surfaceTargets.Count} with declaration changes, {filesChecked} file(s) bound, {timer.ElapsedMilliseconds} ms total{(wholeProjects ? ", whole projects" : "")}");
        return new CheckReport(
            [.. ordered.Take(MaxReported)],
            filesChecked,
            projects,
            [.. surfaceProjects.Select(p => p.Name)],
            dependents.Count,
            wholeProjects);
    }

    /// <summary>Binds files in parallel; semantic models of different documents bind independently.</summary>
    private async Task<List<Diagnostic>> IntroducedInFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<Diagnostic>();
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                foreach (var diagnostic in await IntroducedInFileAsync(path, ct).ConfigureAwait(false))
                    results.Add(diagnostic);
            }).ConfigureAwait(false);
        return [.. results];
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
}
