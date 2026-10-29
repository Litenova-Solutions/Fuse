using System.Diagnostics;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

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
    /// <param name="maxReported">How many errors the answer holds at most; 0 is the existing cap.</param>
    public async Task<CheckResult> CheckAsync(CheckScope scope, string? session, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken, int maxReported = 0)
    {
        // Also drops what a cancelled check left behind: its binding times and its analyzer report are not this check's.
        _introduced.Begin(analyzers, reuse: scope is CheckScope.AllChanges);
        var syncing = phases.Start();
        await _workspace.SyncAsync(TargetResolver.NamedPaths(scope), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        // A file that matches HEAD again is no longer credited to the sessions that wrote it, whichever check sees it first.
        _sessions.Forget(_workspace.Tracker.Changed);

        IReadOnlyList<string> scopedPaths = scope is CheckScope.NamedPaths namedPaths ? ScopeExpansion.DisplaySpecs(_workspace.Root, namedPaths.Named) : [];
        var targets = scope is CheckScope.NamedPaths pathScope
            ? ScopeExpansion.Expand(_workspace, pathScope.Named)
            : _targets.Resolve(scope, scope is CheckScope.AllChanges && session is not null ? _sessions.OnlyOtherSessionsWrote(_workspace.Tracker.Changed, session) : null);
        // Named files that are all outside every project, or not C# sources, would check nothing and read as a pass.
        if (targets.Count == 0 && scope is CheckScope.Files { Paths.Count: > 0 } named)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NothingToCheck([.. named.Paths.Select(p => p.Relative)]));
        if (targets.Count == 0 && scope is CheckScope.NamedPaths)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NothingToCheck([.. scopedPaths]));
        if (targets.Count == 0)
            return NoTargets;

        // The sync above has folded in every edit, so what the tree holds now keys the answer a repeat of this check gets.
        // A check of every change answers from a kept answer only when the views hold that tree; an answer bound from
        // older text is never kept under the current tree's key. A check of named files already reads them from disk.
        var fresh = true;
        if (scope is CheckScope.AllChanges)
            fresh = await ViewsMatchDiskAsync(cancellationToken).ConfigureAwait(false);
        var keptKey = fresh ? KeptKeyFor(scope, session, analyzers, maxReported) : null;
        if (keptKey is not null && _kept.TryGet(keptKey, out var kept))
        {
            _workspace.Log("check: kept answer");
            return kept;
        }

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
        var wholeCauses = new List<WholeProjectCause>();
        if (changedIn.Count > 0)
        {
            dependents = changedIn.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
                .Where(p => !changedIn.Any(s => s.Path == p.Path))
                .ToList();
            var loadingDependents = phases.Start();
            await _workspace.EnsureLoadedAsync(dependents, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.LoadDependents, loadingDependents);

            var reached = changedIn.Concat(dependents).ToList();
            var coverage = await RecoverCoverageAsync(reached, cancellationToken).ConfigureAwait(false);
            var searching = phases.Start();
            reach = await _reach.ReachAsync(changedTargets, reached, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.ReferenceSearch, searching);
            LogCoverage(reached, coverage, reach);

            var binding = phases.Start();
            var bound = await _candidates.BindAsync(reach, reached, targets, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.BindCandidates, binding);
            errors.AddRange(bound.Errors);
            filesChecked = bound.FilesChecked;
            wholeProjects = bound.CheckedWholeProjects;
            if (wholeProjects)
            {
                if (reach is Reach.Broad broad)
                    wholeCauses.AddRange(BroadCauses(broad, reached));
                else
                    wholeCauses.AddRange(bound.WholeProjectCauses);
            }
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
        _workspace.Log($"check: binding {compilerMs} ms, analyzers {analyzerMs} ms (summed over files); {targets.Count} target(s) in {targetsMs} ms, {changedTargets.Count} with declaration changes, {filesChecked} file(s) bound{(reused > 0 ? $" ({reused} with errors reused from an earlier check)" : "")}, {timer.ElapsedMilliseconds} ms total{(wholeProjects ? WholeProjectsLog(wholeCauses) : "")}{(leftToOthers > 0 ? $", {leftToOthers} error(s) left to other sessions" : "")}{(notAttributed > 0 ? $", {notAttributed} file(s) not attributed" : "")}{(unfinished > 0 ? $", analyzers unfinished in {unfinished}" : "")}{(leftOut.Count > 0 ? $", left out {string.Join(", ", leftOut)}" : "")}");
        _introduced.LogBaseline();
        var reported = CauseLines.Attach([.. ordered.Take(maxReported == 0 ? MaxReported : maxReported)], reach, root, targets);
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
            ScopedPaths = scopedPaths,
            WholeProjectCauses = wholeCauses,
        };
        if (keptKey is not null)
            _kept.Keep(keptKey, result);
        return result;
    }

    /// <summary>
    ///     Warms the baseline compilation-wide analyzer results of <paramref name="node"/>, one target framework at a
    ///     time, for the background warm after HEAD moved. It fills the same shared runs a check waits for, and reports
    ///     nothing.
    /// </summary>
    /// <returns>True when any target framework of the node has compilation-wide analyzers that were warmed.</returns>
    public async Task<bool> WarmBaselineAsync(ProjectNode node, CancellationToken cancellationToken)
    {
        var warmed = false;
        var generation = _workspace.BaselineGeneration;
        foreach (var project in RepoWorkspace.ProjectsFor(_workspace.Baseline, node).ToList())
            warmed |= await _introduced.WarmBaselineAsync(project, generation, cancellationToken).ConfigureAwait(false);
        return warmed;
    }

    /// <summary>
    ///     The key the kept answer for this check is kept under, from the tree as the sync just left it, or null when
    ///     no answer is kept for it because a changed file cannot be read. The check runs as usual then.
    /// </summary>
    private string? KeptKeyFor(CheckScope scope, string? session, AnalyzerRun analyzers, int maxReported)
    {
        var scopeKey = scope switch
        {
            CheckScope.Files files => "files:" + string.Join(",", files.Paths.Select(p => p.Relative).Order(StringComparer.Ordinal)),
            CheckScope.NamedPaths paths => "paths:" + string.Join(",", ScopeExpansion.DisplaySpecs(_workspace.Root, paths.Named).Order(StringComparer.Ordinal)),
            _ => "all",
        };
        // A paths scope keys by its display specs,
        // distinct from all changes
        // and from any file list.
        // A kept answer holds only the errors its cap allowed, so the cap keys it too.
        var capKey = maxReported == 0 ? "cap:default" : $"cap:{maxReported}";
        return KeptCheck.KeyFor(
            capKey + ":" + scopeKey,
            session,
            analyzers.CompilationWide,
            _workspace.Tracker.Head ?? "",
            _workspace.ConfigurationGeneration,
            _workspace.BaselineGeneration,
            _sessions.RecordsHash(),
            _workspace.Tracker.Changed.Select(path => (path.Relative, path.Absolute)));
    }

    /// <summary>
    ///     Applies every changed file to both views again and compares each with what the disk holds now. A file with no
    ///     document in the current view is not compared: its project is not bound yet.
    /// </summary>
    /// <returns>False when a changed file could not be read or still disagrees, so this answer is not kept.</returns>
    private async Task<bool> ViewsMatchDiskAsync(CancellationToken cancellationToken)
    {
        var paths = _workspace.Tracker.Changed;
        await _workspace.PatchViewsAsync(paths, cancellationToken).ConfigureAwait(false);
        var current = _workspace.Current;
        var stale = 0;
        foreach (var path in paths)
        {
            if (!await MatchesDiskAsync(current, path, cancellationToken).ConfigureAwait(false))
                stale++;
        }

        if (stale > 0)
            _workspace.Log($"check: {stale} changed file(s) could not be read from disk, so this answer is not kept");
        return stale == 0;
    }

    /// <summary>True when every document the current view holds for <paramref name="path"/> has the content the disk holds now.</summary>
    private static async Task<bool> MatchesDiskAsync(Solution current, RepoPath path, CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try
        {
            bytes = File.Exists(path.Absolute) ? File.ReadAllBytes(path.Absolute) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bytes = null;
        }

        if (bytes is null)
            return false;

        var ids = current.GetDocumentIdsWithFilePath(path.Absolute);
        if (ids.IsEmpty)
            return true;
        var disk = SolutionViews.Decode(bytes);
        foreach (var id in ids)
        {
            TextDocument? document = current.GetDocument(id) ?? (TextDocument?)current.GetAdditionalDocument(id);
            if (document is not null && !(await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ContentEquals(disk))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The reached projects with no project in one of the views. A project counts when each view holds at least one
    ///     project built from it: a multi-targeted project is built into one per framework.
    /// </summary>
    internal static IReadOnlyList<ProjectNode> Uncovered(IReadOnlyList<ProjectNode> reached, Solution current, Solution baseline) =>
        [.. reached.Where(node => !RepoWorkspace.ProjectsFor(current, node).Any() || !RepoWorkspace.ProjectsFor(baseline, node).Any())];

    /// <summary>
    ///     Makes a reached project no view holds fail the check instead of narrowing it without saying so: the views are
    ///     derived again once, and what is still missing is reported, as is a load failure a background load recorded for
    ///     a reached project since the check's own load.
    /// </summary>
    /// <returns>How each view held the reached projects before the rebuild, or null when nothing was missing.</returns>
    /// <exception cref="FuseException">A reached project is still missing after the rebuild, or failed to load.</exception>
    private async Task<string?> RecoverCoverageAsync(IReadOnlyList<ProjectNode> reached, CancellationToken cancellationToken)
    {
        if (Uncovered(reached, _workspace.Current, _workspace.Baseline).Count == 0)
        {
            ThrowOnLoadFailure(reached);
            return null;
        }

        var held = HeldLine(reached);
        await _workspace.RebuildViewsAsync(cancellationToken).ConfigureAwait(false);
        var uncovered = Uncovered(reached, _workspace.Current, _workspace.Baseline);
        if (uncovered.Count > 0)
        {
            _workspace.Log($"check coverage: {held}");
            throw CoverageFailed(uncovered[0]);
        }

        ThrowOnLoadFailure(reached);
        return held;
    }

    /// <summary>
    ///     Writes the coverage line for an affected check: one whose views missed a reached project, or whose precise reach
    ///     names a file on disk with no document in the current view. A check neither holds for logs nothing.
    /// </summary>
    private void LogCoverage(IReadOnlyList<ProjectNode> reached, string? coverage, Reach reach)
    {
        var missing = reach is Reach.Precise precise ? CandidatesWithoutDocument(precise) : [];
        if (coverage is null && missing.Count == 0)
            return;
        var line = $"check coverage: {coverage ?? HeldLine(reached)}";
        if (coverage is not null)
            line += "; recovered after rebuilding the views";
        if (missing.Count > 0)
            line += $"; candidates without a document: {string.Join(",", missing)}";
        _workspace.Log(line);
    }

    /// <summary>The relative paths of <paramref name="precise"/>'s candidates on disk with no document in the current view, in order.</summary>
    private List<string> CandidatesWithoutDocument(Reach.Precise precise)
    {
        var current = _workspace.Current;
        return [.. precise.Causes.Keys
            .Where(path => File.Exists(path.Absolute) && current.GetDocumentIdsWithFilePath(path.Absolute).IsEmpty)
            .Select(path => path.Relative)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Which reached projects each view holds a project for, in reached order, as the coverage line names them.</summary>
    private string HeldLine(IReadOnlyList<ProjectNode> reached)
    {
        var current = _workspace.Current;
        var baseline = _workspace.Baseline;
        return $"reached {CoverageNames(reached)}; current holds {CoverageNames(reached.Where(node => RepoWorkspace.ProjectsFor(current, node).Any()))}; baseline holds {CoverageNames(reached.Where(node => RepoWorkspace.ProjectsFor(baseline, node).Any()))}";
    }

    private static string CoverageNames(IEnumerable<ProjectNode> nodes) =>
        string.Join(",", nodes.Select(node => node.Name));

    private static FuseException CoverageFailed(ProjectNode node) =>
        new(ErrorCode.LoadFailed, $"could not load {node.Path.Relative}: the check reached {node.Name} but the engine's view does not hold it (the projects the view holds are named in engine.log)");

    /// <summary>Reports a load failure a background load recorded for a reached project since the check's own load.</summary>
    /// <exception cref="FuseException">A reached project failed to load.</exception>
    private void ThrowOnLoadFailure(IReadOnlyList<ProjectNode> reached)
    {
        foreach (var node in reached)
        {
            if (_workspace.LoadFailureOf(node) is { } failure)
                throw new FuseException(ErrorCode.LoadFailed, $"could not load {node.Path.Relative}: {failure}");
        }
    }

    private static IReadOnlyList<WholeProjectCause> BroadCauses(Reach.Broad broad, IReadOnlyList<ProjectNode> reached)
    {
        var pairs = (broad.BroadCauses ?? [])
            .Select(b => (File: b.File.Relative, Cause: b.Cause is Cause.Removed ? $"removed: {b.Cause.Declaration}" : $"changed: {b.Cause.Declaration}"))
            .Distinct()
            .OrderBy(p => p.File, StringComparer.Ordinal)
            .ThenBy(p => p.Cause, StringComparer.Ordinal)
            .ToList();
        var files = pairs.Select(p => p.File).Distinct(StringComparer.Ordinal).ToList();
        var causes = pairs.Select(p => p.Cause).Distinct(StringComparer.Ordinal).ToList();
        return [.. reached.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => new WholeProjectCause(n.Name, files, causes))];
    }

    private static string WholeCause(WholeProjectCause cause)
    {
        if (cause.ChangedFiles.Count == 0)
            return cause.Project;
        var files = string.Join(", ", cause.ChangedFiles);
        return cause.Causes.Count == 0 ? $"{cause.Project} because {files}" : $"{cause.Project} because {files} ({string.Join("; ", cause.Causes)})";
    }

    private static string WholeProjectsLog(List<WholeProjectCause> causes)
    {
        if (causes.Count == 0)
            return ", whole projects";
        return ", whole projects: " + string.Join("; ", causes.Select(WholeCause));
        // The log holds every project and file.
    }

    private static List<ProjectNode> OwnersOf(RepoGraph graph, IEnumerable<RepoPath> paths) =>
        paths.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
}
