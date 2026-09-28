using Fuse.Engine;
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
    /// <param name="phases">Collects how long each phase of this check took; null collects nothing.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<CheckReport> CheckAsync(IReadOnlyCollection<string>? files, PhaseTimes? phases, CancellationToken cancellationToken)
    {
        var reports = await CheckManyAsync([files], phases is null ? null : [phases], cancellationToken).ConfigureAwait(false);
        return reports[0];
    }

    /// <summary>
    ///     Answers several checks at once, sharing the work they have in common. One sync, one load and one binding pass
    ///     cover the union of every client's targets and candidates; each client is then given the errors in its own
    ///     targets and the candidates its own change reaches, with its own scope line, so its answer is the answer a serial
    ///     check would have given it. A scope of null means every change since HEAD, and one such client makes the batch's
    ///     target set the whole working tree.
    /// </summary>
    /// <param name="scopes">Files per client, or null for a client checking every change.</param>
    /// <param name="perClient">A collector per client, or null entries for clients that are not measuring. Work the batch shares is recorded on every client, because each of them waited for it.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    public async Task<IReadOnlyList<CheckReport>> CheckManyAsync(
        IReadOnlyList<IReadOnlyCollection<string>?> scopes,
        IReadOnlyList<PhaseTimes?>? perClient,
        CancellationToken cancellationToken)
    {
        var root = _workspace.Root;
        var graph = _workspace.Graph;
        var known = scopes.Where(s => s is not null).SelectMany(s => s!).Select(root.Absolute).Distinct(ChangeTracker.PathComparer).ToList();
        IReadOnlyList<PhaseTimes?> collectors = perClient ?? [];

        var syncing = System.Diagnostics.Stopwatch.StartNew();
        await _workspace.SyncAsync(known, cancellationToken).ConfigureAwait(false);
        PhaseTimes.AddToAll(collectors, "sync", syncing);

        // A client checking every change takes every change; a client with files takes its own.
        var targets = new List<string>();
        foreach (var scope in scopes)
            targets.AddRange(TargetsFor(scope));
        targets = targets.Distinct(ChangeTracker.PathComparer).ToList();

        if (targets.Count == 0)
            return [.. scopes.Select(_ => new CheckReport([], 0, [], [], 0, false))];

        var owners = targets.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        var loading = System.Diagnostics.Stopwatch.StartNew();
        await _workspace.EnsureLoadedAsync(owners, cancellationToken).ConfigureAwait(false);
        PhaseTimes.AddToAll(collectors, "load", loading);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var introducedByFile = await IntroducedInFilesAsync(targets, cancellationToken).ConfigureAwait(false);
        PhaseTimes.AddToAll(collectors, "bindTargets", timer);
        var targetsMs = timer.ElapsedMilliseconds;

        // Which owning projects have declaration changes (syntax only), then what those changes can reach.
        var diffing = System.Diagnostics.Stopwatch.StartNew();
        var surfaceTargets = new List<string>();
        foreach (var path in targets)
        {
            if (await _reach.HasSurfaceChangeAsync(path, cancellationToken).ConfigureAwait(false))
                surfaceTargets.Add(path);
        }

        PhaseTimes.AddToAll(collectors, "surfaceDiff", diffing);
        var surfaceSet = surfaceTargets.ToHashSet(ChangeTracker.PathComparer);
        var surfaceProjects = surfaceTargets.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        var dependents = new List<ProjectNode>();
        var reachNodes = new List<ProjectNode>();
        var reach = new List<Project>();
        if (surfaceProjects.Count > 0)
        {
            var loadingDependents = System.Diagnostics.Stopwatch.StartNew();
            dependents = surfaceProjects.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
                .Where(p => !surfaceProjects.Any(s => ChangeTracker.PathComparer.Equals(s.Path, p.Path)))
                .ToList();
            await _workspace.EnsureLoadedAsync(dependents, cancellationToken).ConfigureAwait(false);
            PhaseTimes.AddToAll(collectors, "load", loadingDependents);
            reachNodes.AddRange(surfaceProjects.Concat(dependents));
            reach.AddRange(reachNodes.SelectMany(n => RepoWorkspace.ProjectsFor(_workspace.Current, n)));
        }

        // Each client's own candidates come from its own declaration changes; the reference sets behind them are cached, so
        // the second client through costs a lookup rather than a search.
        var own = new List<Own>(scopes.Count);
        for (var index = 0; index < scopes.Count; index++)
        {
            var scope = scopes[index];
            var ownTargets = TargetsFor(scope);
            var ownSurface = ownTargets.Where(surfaceSet.Contains).ToList();
            var ownSurfaceProjects = ownSurface.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
            var ownDependents = ownSurfaceProjects.Count == 0
                ? []
                : ownSurfaceProjects.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
                    .Where(p => !ownSurfaceProjects.Any(s => ChangeTracker.PathComparer.Equals(s.Path, p.Path))).ToList();
            var ownReachNodes = ownSurfaceProjects.Concat(ownDependents).ToList();
            var ownReach = ownReachNodes.SelectMany(n => RepoWorkspace.ProjectsFor(_workspace.Current, n)).ToList();
            var searching = ownSurfaceProjects.Count == 0 ? null : System.Diagnostics.Stopwatch.StartNew();
            var reached = searching is null
                ? null
                : await _reach.ProvenanceAsync(ownSurface, ownReach, cancellationToken).ConfigureAwait(false);
            // Recorded only when a search ran, so a body-only edit does not report a reference search it did not do.
            if (searching is not null && perClient is not null && index < perClient.Count)
                perClient[index]?.Add("referenceSearch", searching.Elapsed.TotalMilliseconds);
            var ownTargetSet = ownTargets.ToHashSet(ChangeTracker.PathComparer);
            var candidates = reached is null && ownSurfaceProjects.Count > 0
                ? ownReach.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>().Distinct(ChangeTracker.PathComparer).ToList()
                : reached?.Files.ToList() ?? [];
            candidates.RemoveAll(ownTargetSet.Contains);
            own.Add(new(ownTargets, ownSurfaceProjects, ownDependents, ownReachNodes, ownReach, reached, candidates));
        }

        // One binding pass over the union of every client's candidates, and one whole-project pass for the clients whose
        // own candidate set is too large to enumerate.
        var binding = System.Diagnostics.Stopwatch.StartNew();
        var wide = own.Where(o => o.Candidates.Count > WholeProjectThreshold).ToList();
        var allCandidates = own.Where(o => o.Candidates.Count <= WholeProjectThreshold)
            .SelectMany(o => o.Candidates).Distinct(ChangeTracker.PathComparer).ToList();
        var introducedByCandidate = await IntroducedInFilesAsync(allCandidates, cancellationToken).ConfigureAwait(false);
        var wholeNodes = wide.SelectMany(o => o.ReachNodes).DistinctBy(p => p.Path).ToList();
        var introducedByProject = new List<Diagnostic>();
        foreach (var node in wholeNodes)
            introducedByProject.AddRange(await IntroducedInProjectAsync(node, cancellationToken).ConfigureAwait(false));
        PhaseTimes.AddToAll(collectors, "bindCandidates", binding);

        var (compilerMs, analyzerMs) = _collector.TakeTimings();
        _workspace.Log($"check: {scopes.Count} client(s) in one pass; binding {compilerMs} ms, analyzers {analyzerMs} ms (summed over files); {targets.Count} target(s) in {targetsMs} ms, {surfaceTargets.Count} with declaration changes, {targets.Count + allCandidates.Count} file(s) bound, {timer.ElapsedMilliseconds} ms total");

        var reports = new List<CheckReport>(own.Count);
        foreach (var client in own)
        {
            var introduced = new List<Diagnostic>();
            foreach (var path in client.Targets)
                introduced.AddRange(introducedByFile.GetValueOrDefault(path) ?? []);
            int filesChecked = client.Targets.Count;
            var wholeProjects = client.Candidates.Count > WholeProjectThreshold;
            if (wholeProjects)
            {
                var paths = client.ReachProjects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>().ToHashSet(ChangeTracker.PathComparer);
                foreach (var node in client.ReachNodes)
                    introduced.AddRange(introducedByProject.Where(d => paths.Contains(root.Absolute(d.Path))));
                filesChecked = client.ReachProjects.Sum(p => p.DocumentIds.Count);
            }
            else
            {
                foreach (var path in client.Candidates)
                    introduced.AddRange(introducedByCandidate.GetValueOrDefault(path) ?? []);
                filesChecked += client.Candidates.Count;
            }

            var ordered = introduced.Distinct()
                .OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column)
                .ToList();
            var projects = ordered
                .Select(d => graph.OwnersOf(root.Absolute(d.Path)) is [var owner, ..] ? owner.Name : null)
                .OfType<string>()
                .Distinct()
                .ToArray();
            var reported = ordered.Take(MaxReported).ToArray();
            var (context, contextLeftOut) = ErrorContext.Build(reported, client.Provenance ?? new ReachProvenance(), root.Absolute, client.Targets);
            reports.Add(new CheckReport(
                reported,
                filesChecked,
                projects,
                [.. client.SurfaceProjects.Select(p => p.Name)],
                client.Dependents.Count,
                wholeProjects,
                context,
                contextLeftOut));
        }

        return reports;
    }

    /// <summary>The source files a scope of null (every change) or a set of files resolves to, in the order given.</summary>
    private List<string> TargetsFor(IReadOnlyCollection<string>? files)
    {
        var graph = _workspace.Graph;
        return (files is null ? _workspace.Tracker.Changed : files.Select(_workspace.Root.Absolute).ToList())
            .Where(ChangeTracker.IsSource)
            .Distinct(ChangeTracker.PathComparer)
            .Where(p => graph.OwnersOf(p).Count > 0)
            .ToList();
    }

    /// <summary>One client's own slice of a batch: its targets, and what its own change reaches.</summary>
    private sealed record Own(
        List<string> Targets,
        List<ProjectNode> SurfaceProjects,
        List<ProjectNode> Dependents,
        List<ProjectNode> ReachNodes,
        List<Project> ReachProjects,
        ReachProvenance? Provenance,
        List<string> Candidates);

    /// <summary>Writes a batch failure to the engine log, so a broken batch is not a client that never gets an answer.</summary>
    internal void LogBatchFailure(Exception e) => _workspace.Log($"check batch failed: {e}");

    /// <summary>Binds files in parallel and groups the errors by file; semantic models of different documents bind independently.</summary>
    private async Task<IReadOnlyDictionary<string, List<Diagnostic>>> IntroducedInFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<KeyValuePair<string, IReadOnlyList<Diagnostic>>>();
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
                results.Add(new KeyValuePair<string, IReadOnlyList<Diagnostic>>(path, [.. await IntroducedInFileAsync(path, ct).ConfigureAwait(false)]))).ConfigureAwait(false);
        var byFile = new Dictionary<string, List<Diagnostic>>(ChangeTracker.PathComparer);
        foreach (var (path, diagnostics) in results)
        {
            if (diagnostics.Count == 0)
                continue;
            if (!byFile.TryGetValue(path, out var list))
                byFile[path] = list = [];
            list.AddRange(diagnostics);
        }

        return byFile;
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
