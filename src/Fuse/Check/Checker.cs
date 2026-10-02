using System.Diagnostics;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Workspace;

namespace Fuse.Check;

/// <summary>
///     Reports the errors the working tree has that HEAD does not, in the targets and in every file a declaration change
///     can reach, including files in dependent projects. It composes the steps below in order and records each as a
///     <see cref="Phase"/>. It is not safe to call concurrently; the engine calls it one request at a time, behind its
///     request lock.
/// </summary>
/// <remarks>
///     The check grows only as far as the change requires:
///     <list type="number">
///         <item><see cref="TargetResolver"/> turns the scope into targets, and their owning projects load.</item>
///         <item><see cref="IntroducedErrors"/> binds every target and keeps the errors HEAD does not have.</item>
///         <item>
///             <see cref="ChangeReach"/> finds the targets with declaration changes. With none (a body-only edit),
///             nothing else can gain an error and the check ends there.
///         </item>
///         <item>
///             Otherwise the dependents of the projects with declaration changes load, <see cref="ChangeReach"/> turns
///             the changes into a <see cref="Reach"/>, and <see cref="CandidateBinding"/> binds what it names.
///         </item>
///         <item><see cref="CauseLines"/> gives each error in a candidate the declaration change that reached it.</item>
///     </list>
/// </remarks>
internal sealed class Checker
{
    private const int MaxReported = 200;

    private static readonly CheckResult NoTargets = new([], 0, [], [], 0, false, 0, 0);

    private readonly RepoWorkspace _workspace;
    private readonly TargetResolver _targets;
    private readonly IntroducedErrors _introduced;
    private readonly ChangeReach _reach;
    private readonly CandidateBinding _candidates;

    public Checker(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _targets = new TargetResolver(workspace);
        _introduced = new IntroducedErrors(workspace);
        _reach = new ChangeReach(workspace);
        _candidates = new CandidateBinding(workspace, _introduced);
    }

    /// <param name="scope">The files to check, or every change since HEAD.</param>
    /// <param name="phases">Collects how long each phase of this check took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<CheckResult> CheckAsync(CheckScope scope, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        await _workspace.SyncAsync(TargetResolver.NamedPaths(scope), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);

        var targets = _targets.Resolve(scope);
        // Named files that are all outside every project, or not C# sources, would check nothing and read as a pass.
        if (targets.Count == 0 && scope is CheckScope.Files { Paths.Count: > 0 } named)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NothingToCheck([.. named.Paths.Select(p => p.Relative)]));
        if (targets.Count == 0)
            return NoTargets;

        var graph = _workspace.Graph;
        var loading = phases.Start();
        await _workspace.EnsureLoadedAsync(OwnersOf(graph, targets), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Load, loading);

        var timer = Stopwatch.StartNew();
        var errors = await _introduced.InFilesAsync(targets, cancellationToken).ConfigureAwait(false);
        var targetsMs = timer.ElapsedMilliseconds;
        phases.Add(Phase.BindTargets, timer);

        // Which targets have declaration changes (syntax only), then what those changes can reach.
        var diffing = phases.Start();
        var changedTargets = new List<RepoPath>();
        foreach (var path in targets)
        {
            if (await _reach.HasDeclarationChangeAsync(path, cancellationToken).ConfigureAwait(false))
                changedTargets.Add(path);
        }

        phases.Add(Phase.SurfaceDiff, diffing);
        var changedIn = OwnersOf(graph, changedTargets);
        var dependents = new List<ProjectNode>();
        Reach reach = new Reach.None();
        var filesChecked = targets.Count;
        var wholeProjects = false;
        if (changedIn.Count > 0)
        {
            dependents = changedIn.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
                .Where(p => !changedIn.Any(s => s.Path == p.Path))
                .ToList();
            var loadingDependents = phases.Start();
            await _workspace.EnsureLoadedAsync(dependents, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.LoadDependents, loadingDependents);

            var reached = changedIn.Concat(dependents).ToList();
            var searching = phases.Start();
            reach = await _reach.ReachAsync(changedTargets, reached, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.ReferenceSearch, searching);

            var binding = phases.Start();
            var bound = await _candidates.BindAsync(reach, reached, targets, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.BindCandidates, binding);
            errors.AddRange(bound.Errors);
            filesChecked = bound.FilesChecked;
            wholeProjects = bound.CheckedWholeProjects;
        }

        var root = _workspace.Root;
        var ordered = errors.Distinct()
            .OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column)
            .ToList();
        var projects = ordered
            .Select(d => graph.OwnersOf(root.PathOf(d.Path)) is [var owner, ..] ? owner.Name : null)
            .OfType<string>()
            .Distinct()
            .ToArray();
        var (compilerMs, analyzerMs) = _introduced.TakeTimings();
        _workspace.Log($"check: binding {compilerMs} ms, analyzers {analyzerMs} ms (summed over files); {targets.Count} target(s) in {targetsMs} ms, {changedTargets.Count} with declaration changes, {filesChecked} file(s) bound, {timer.ElapsedMilliseconds} ms total{(wholeProjects ? ", whole projects" : "")}");
        var reported = CauseLines.Attach([.. ordered.Take(MaxReported)], reach, root, targets);
        return new CheckResult(
            reported,
            filesChecked,
            projects,
            [.. changedIn.Select(p => p.Name)],
            dependents.Count,
            wholeProjects,
            ordered.Count,
            ordered.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count());
    }

    private static List<ProjectNode> OwnersOf(RepoGraph graph, IEnumerable<RepoPath> paths) =>
        paths.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
}
