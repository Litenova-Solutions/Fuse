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
///         <item>
///             When the check is answered to a session, <see cref="SessionAttribution"/> leaves out the errors other
///             sessions' edits caused without it.
///         </item>
///         <item><see cref="CauseLines"/> gives each error in a candidate the declaration change that reached it.</item>
///         <item>
///             <see cref="GeneratedSource"/> names the file a build generates that declares what an error is about, which
///             may only be older than its source.
///         </item>
///     </list>
/// </remarks>
internal sealed class Checker
{
    private const int MaxReported = 200;

    private static readonly CheckResult NoTargets = new([], 0, [], [], 0, false, 0, 0, 0);

    private readonly RepoWorkspace _workspace;
    private readonly TargetResolver _targets;
    private readonly IntroducedErrors _introduced;
    private readonly ChangeReach _reach;
    private readonly CandidateBinding _candidates;
    private readonly SessionEdits _sessions;
    private readonly SessionAttribution _attribution;
    private readonly GeneratedSource _generated;
    private readonly KeptCheck _kept = new();

    /// <param name="workspace">The repository's projects and views.</param>
    /// <param name="sessions">Which sessions wrote each changed file; a check answered to a session reads it.</param>
    public Checker(RepoWorkspace workspace, SessionEdits sessions)
    {
        _workspace = workspace;
        _targets = new TargetResolver(workspace);
        _introduced = new IntroducedErrors(workspace);
        _reach = new ChangeReach(workspace);
        _candidates = new CandidateBinding(workspace, _introduced);
        _sessions = sessions;
        _attribution = new SessionAttribution(workspace, _introduced, sessions);
        _generated = new GeneratedSource(workspace.Root);
    }

    /// <param name="scope">The files to check, or every change since HEAD.</param>
    /// <param name="phases">Collects how long each phase of this check took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public Task<CheckResult> CheckAsync(CheckScope scope, PhaseTimes phases, CancellationToken cancellationToken) =>
        CheckAsync(scope, null, phases, cancellationToken);

    /// <param name="scope">The files to check, or every change since HEAD.</param>
    /// <param name="session">
    ///     The session the answer is for, or null to answer with every error. A session is told only the errors its own
    ///     edits cause, alone or together with other sessions' edits (<see cref="SessionAttribution"/>).
    /// </param>
    /// <param name="phases">Collects how long each phase of this check took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public Task<CheckResult> CheckAsync(CheckScope scope, string? session, PhaseTimes phases, CancellationToken cancellationToken) =>
        CheckAsync(scope, session, AnalyzerRun.Full, phases, cancellationToken);

    /// <param name="scope">The files to check, or every change since HEAD.</param>
    /// <param name="session">The session the answer is for, or null to answer with every error.</param>
    /// <param name="analyzers">How analyzers run on the working tree.</param>
    /// <param name="phases">Collects how long each phase of this check took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<CheckResult> CheckAsync(CheckScope scope, string? session, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        // Also drops what a cancelled check left behind: its binding times and its analyzer report are not this check's.
        _introduced.Begin(analyzers, reuse: scope is CheckScope.AllChanges);
        var syncing = phases.Start();
        await _workspace.SyncAsync(TargetResolver.NamedPaths(scope), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        // A file that matches HEAD again is no longer credited to the sessions that wrote it, whichever check sees it first.
        _sessions.Forget(_workspace.Tracker.Changed);

        var targets = _targets.Resolve(scope);
        // Named files that are all outside every project, or not C# sources, would check nothing and read as a pass.
        if (targets.Count == 0 && scope is CheckScope.Files { Paths.Count: > 0 } named)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NothingToCheck([.. named.Paths.Select(p => p.Relative)]));
        if (targets.Count == 0)
            return NoTargets;

        // The sync above has folded in every edit, so what the tree holds now keys the answer a repeat of this check gets.
        var keptKey = KeptKeyFor(scope, session, analyzers);
        if (keptKey is not null && _kept.TryGet(keptKey, out var kept))
            return kept;

        var graph = _workspace.Graph;
        var loading = phases.Start();
        await _workspace.EnsureLoadedAsync(OwnersOf(graph, targets), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Load, loading);

        var timer = Stopwatch.StartNew();
        var errors = await _introduced.InFilesAsync(targets, cancellationToken).ConfigureAwait(false);
        var targetsMs = timer.ElapsedMilliseconds;
        phases.Add(Phase.BindTargets, timer);
        var parts = _introduced.TakeBindParts();
        phases.Add(Phase.BindUpdate, parts.UpdateMs);
        phases.Add(Phase.BindCompiler, parts.CompilerMs);
        phases.Add(Phase.BindAnalyzers, parts.AnalyzerMs);
        phases.Add(Phase.BindFrameworks, parts.Documents);

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
        var leftToOthers = 0;
        var notAttributed = 0;
        if (session is not null)
        {
            var attributing = phases.Start();
            if (await _attribution.ToldAsync(ordered, session, cancellationToken).ConfigureAwait(false) is ({ } told, var filesNotAttributed))
            {
                leftToOthers = ordered.Count - told.Count;
                notAttributed = filesNotAttributed;
                ordered = told;
                phases.Add(Phase.Attribution, attributing);
            }
        }

        var projects = ordered
            .Select(d => graph.OwnersOf(root.PathOf(d.Path)) is [var owner, ..] ? owner.Name : null)
            .OfType<string>()
            .Distinct()
            .ToArray();
        var (compilerMs, analyzerMs) = _introduced.TakeTimings();
        compilerMs += parts.CompilerMs;
        analyzerMs += parts.AnalyzerMs;
        var (unfinished, leftOut) = _introduced.TakeAnalyzerReport();
        var reused = _introduced.TakeReused();
        _workspace.Log($"check: binding {compilerMs} ms, analyzers {analyzerMs} ms (summed over files); {targets.Count} target(s) in {targetsMs} ms, {changedTargets.Count} with declaration changes, {filesChecked} file(s) bound{(reused > 0 ? $" ({reused} with errors reused from an earlier check)" : "")}, {timer.ElapsedMilliseconds} ms total{(wholeProjects ? ", whole projects" : "")}{(leftToOthers > 0 ? $", {leftToOthers} error(s) left to other sessions" : "")}{(notAttributed > 0 ? $", {notAttributed} file(s) not attributed" : "")}{(unfinished > 0 ? $", analyzers unfinished in {unfinished}" : "")}{(leftOut.Count > 0 ? $", left out {string.Join(", ", leftOut)}" : "")}");
        var reported = CauseLines.Attach([.. ordered.Take(MaxReported)], reach, root, targets);
        reported = await _generated.AttachAsync(_workspace.Current, reported, cancellationToken).ConfigureAwait(false);
        var result = new CheckResult(
            reported,
            filesChecked,
            projects,
            [.. changedIn.Select(p => p.Name)],
            dependents.Count,
            wholeProjects,
            ordered.Count,
            ordered.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count(),
            leftToOthers)
        {
            AnalyzersUnfinished = unfinished,
            CompilationWideLeftOut = leftOut,
            FilesNotAttributed = notAttributed,
        };
        if (keptKey is not null)
            _kept.Keep(keptKey, result);
        return result;
    }

    /// <summary>
    ///     The key the kept answer for this check is kept under, from the tree as the sync just left it, or null when
    ///     no answer is kept for it because a changed file cannot be read. The check runs as usual then.
    /// </summary>
    private string? KeptKeyFor(CheckScope scope, string? session, AnalyzerRun analyzers)
    {
        var scopeKey = scope is CheckScope.Files files
            ? "files:" + string.Join(",", files.Paths.Select(p => p.Relative).Order(StringComparer.Ordinal))
            : "all";
        return KeptCheck.KeyFor(
            scopeKey,
            session,
            analyzers.CompilationWide,
            _workspace.Tracker.Head ?? "",
            _workspace.ConfigurationGeneration,
            _workspace.BaselineGeneration,
            _sessions.RecordsHash(),
            _workspace.Tracker.Changed.Select(path => (path.Relative, path.Absolute)));
    }

    private static List<ProjectNode> OwnersOf(RepoGraph graph, IEnumerable<RepoPath> paths) =>
        paths.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
}
