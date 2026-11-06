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
///     <see cref="Phase"/>. It is safe to call concurrently: each call pins a <see cref="Generation"/> and holds a
///     <see cref="CheckContext"/> of its own, and the engine still calls it one request at a time behind its request
///     lock in this wave.
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

    /// <summary>
    ///     Past this many changed files the kept answer is skipped: hashing every file costs more than the reuse saves.
    ///     The same number as the whole-project threshold, so one number to remember.
    /// </summary>
    private const int MaxKeptHashFiles = 500;

    /// <summary>How many sessions the engine log names per check before counting the rest.</summary>
    private const int MaxLoggedSessions = 10;

    /// <summary>How many times one check restarts on a newer generation after coverage recovery, before it reports the missing project.</summary>
    private const int MaxCoverageRestarts = 2;

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
    /// <param name="maxReported">How many errors the answer holds at most; 0 keeps the default cap.</param>
    public async Task<CheckResult> CheckAsync(CheckScope scope, string? session, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken, int maxReported = 0)
    {
        for (var restart = 0; ; restart++)
        {
            try
            {
                return await CheckOnceAsync(scope, session, analyzers, phases, cancellationToken, maxReported).ConfigureAwait(false);
            }
            catch (CoverageRestartException) when (restart < MaxCoverageRestarts)
            {
                // The writer published a generation that holds the reached project this pin lacked. The next
                // attempt pins it and answers from it alone.
            }
        }
    }

    private async Task<CheckResult> CheckOnceAsync(CheckScope scope, string? session, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken, int maxReported)
    {
        var context = new CheckContext(analyzers, reuse: scope is CheckScope.AllChanges);
        _introduced.BeginContext(context);
        var syncing = phases.Start();
        IReadOnlyList<RepoPath> known = scope is CheckScope.NamedPaths scoped ? ScopeExpansion.RecentlyWritten(_workspace.Root, scoped.Named) : TargetResolver.NamedPaths(scope);
        var generation = await _workspace.SyncAndPinAsync(known, cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        // A file that matches HEAD again is no longer credited to the sessions that wrote it, whichever check sees it first.
        _sessions.Forget(generation.Changed);

        IReadOnlyList<string> scopedPaths = scope is CheckScope.NamedPaths namedPaths ? ScopeExpansion.DisplaySpecs(_workspace.Root, namedPaths.Named) : [];
        var targets = scope is CheckScope.NamedPaths pathScope
            ? ScopeExpansion.Expand(_workspace, generation, pathScope.Named)
            : _targets.Resolve(scope, generation, scope is CheckScope.AllChanges && session is not null ? _sessions.OnlyOtherSessionsWrote(generation.Changed, session) : null);
        // Named files that are all outside every project, or not C# sources, would check nothing and read as a pass.
        if (targets.Count == 0 && scope is CheckScope.Files { Paths.Count: > 0 } named)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NothingToCheck([.. named.Paths.Select(p => p.Relative)]));
        if (targets.Count == 0 && scope is CheckScope.NamedPaths)
            throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NothingToCheck([.. scopedPaths]));
        if (targets.Count == 0)
            return NoTargets with { NonCSharpChanged = generation.NonSourceChanged, NonCSharpSample = [.. generation.NonSourceSample.Select(p => p.Relative)] };

        // The sync above has folded in every edit, so what the pinned generation holds keys the answer a repeat of
        // this check gets. A check of every change answers from a kept answer only when its views hold the tree on
        // disk; an answer bound from older text is never kept under the current tree's key.
        var fresh = true;
        if (scope is CheckScope.AllChanges)
        {
            // The writer reapplies every changed file, so an edit the watcher has not reported yet is in the
            // views this check binds. Nothing has bound yet, so the check adopts the generation that round
            // published instead of restarting.
            generation = await _workspace.PatchAndPinAsync(generation.Changed, cancellationToken).ConfigureAwait(false);
            fresh = await ViewsMatchDiskAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        // Past the bound the kept answer costs more hashing than it saves; the check runs as usual.
        var changedFiles = generation.Changed.Count;
        string? keptKey = null;
        if (changedFiles > MaxKeptHashFiles)
            _workspace.Log($"check: kept answer skipped, {changedFiles} changed file(s) past {MaxKeptHashFiles}");
        else if (fresh)
            keptKey = KeptKeyFor(scope, session, analyzers, maxReported, generation);
        if (keptKey is not null && _kept.TryGet(keptKey, out var kept))
        {
            _workspace.Log("check: kept answer");
            return kept;
        }

        var loading = phases.Start();
        generation = await _workspace.EnsureLoadedAndPinAsync(OwnersOf(generation.Graph, targets), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Load, loading);

        var timer = Stopwatch.StartNew();
        var errors = await _introduced.InFilesAsync(targets, generation, context, cancellationToken).ConfigureAwait(false);
        var targetsMs = timer.ElapsedMilliseconds;
        phases.Add(Phase.BindTargets, timer);
        var parts = context.TakeBindParts();
        phases.Add(Phase.BindUpdate, parts.UpdateMs);
        phases.Add(Phase.BindCompiler, parts.CompilerMs);
        phases.Add(Phase.BindAnalyzers, parts.AnalyzerMs);
        phases.Add(Phase.BindFrameworks, parts.Documents);

        // Which targets have declaration changes (syntax only), then what those changes can reach.
        var diffing = phases.Start();
        var changedTargets = new List<RepoPath>();
        foreach (var path in targets)
        {
            if (await _reach.HasDeclarationChangeAsync(path, generation, cancellationToken).ConfigureAwait(false))
                changedTargets.Add(path);
        }

        phases.Add(Phase.SurfaceDiff, diffing);
        var changedIn = OwnersOf(generation.Graph, changedTargets);
        var dependents = new List<ProjectNode>();
        Reach reach = new Reach.None();
        var filesChecked = targets.Count;
        var wholeProjects = false;
        var wholeCauses = new List<WholeProjectCause>();
        if (changedIn.Count > 0)
        {
            dependents = changedIn.SelectMany(generation.Graph.DependentsOf).DistinctBy(p => p.Path)
                .Where(p => !changedIn.Any(s => s.Path == p.Path))
                .ToList();
            var loadingDependents = phases.Start();
            generation = await _workspace.EnsureLoadedAndPinAsync(dependents, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.LoadDependents, loadingDependents);

            var reached = changedIn.Concat(dependents).ToList();
            var coverage = await RecoverCoverageAsync(reached, generation, cancellationToken).ConfigureAwait(false);
            var searching = phases.Start();
            reach = await _reach.ReachAsync(changedTargets, reached, generation, cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.ReferenceSearch, searching);
            LogCoverage(reached, coverage, reach, generation);

            var binding = phases.Start();
            var bound = await _candidates.BindAsync(reach, reached, targets, generation, context, cancellationToken).ConfigureAwait(false);
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
        var graph = generation.Graph;
        var ordered = errors.Distinct()
            .OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column)
            .ToList();
        var leftToOthers = 0;
        var notAttributed = 0;
        if (session is not null)
        {
            var attributing = phases.Start();
            if (await _attribution.ToldAsync(ordered, session, generation, context, cancellationToken).ConfigureAwait(false) is ({ } told, var filesNotAttributed))
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
        var (compilerMs, analyzerMs) = context.TakeTimings();
        compilerMs += parts.CompilerMs;
        analyzerMs += parts.AnalyzerMs;
        var (unfinished, leftOut) = context.TakeAnalyzerReport();
        var reused = context.TakeReused();
        _workspace.Log($"check: binding {compilerMs} ms, analyzers {analyzerMs} ms (summed over files); {targets.Count} target(s) in {targetsMs} ms, {changedTargets.Count} with declaration changes, {filesChecked} file(s) bound{(reused > 0 ? $" ({reused} with errors reused from an earlier check)" : "")}, {timer.ElapsedMilliseconds} ms total{(wholeProjects ? WholeProjectsLog(wholeCauses) : "")}{(leftToOthers > 0 ? $", {leftToOthers} error(s) left to other sessions" : "")}{(notAttributed > 0 ? $", {notAttributed} file(s) not attributed" : "")}{(unfinished > 0 ? $", analyzers unfinished in {unfinished}" : "")}{(leftOut.Count > 0 ? $", left out {string.Join(", ", leftOut)}" : "")}{SessionCounts(generation)}");
        _introduced.LogBaseline(context);
        var reported = CauseLines.Attach(ordered, reach, root, targets);
        reported = [.. reported.Take(maxReported == 0 ? MaxReported : maxReported)];
        reported = await _generated.AttachAsync(generation.Current, reported, cancellationToken).ConfigureAwait(false);
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
            NonCSharpChanged = generation.NonSourceChanged,
            NonCSharpSample = [.. generation.NonSourceSample.Select(p => p.Relative)],
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
        var generation = _workspace.PinGeneration();
        var context = new CheckContext(AnalyzerRun.Full, reuse: false);
        var warmed = false;
        foreach (var project in RepoWorkspace.ProjectsFor(generation.Baseline, node).ToList())
        {
            if (_workspace.PinGeneration().BaselineGeneration != generation.BaselineGeneration)
                return warmed;
            warmed |= await _introduced.WarmBaselineAsync(project, generation.BaselineGeneration, context, cancellationToken).ConfigureAwait(false);
        }

        return warmed;
    }

    /// <summary>
    ///     The key the kept answer for this check is kept under, from the pinned generation, or null when no answer is
    ///     kept for it because a changed file cannot be read. The check runs as usual then.
    /// </summary>
    private string? KeptKeyFor(CheckScope scope, string? session, AnalyzerRun analyzers, int maxReported, Generation generation)
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
            generation.Head ?? "",
            generation.ConfigurationGeneration,
            generation.BaselineGeneration,
            _sessions.RecordsHash(),
            generation.Changed.Select(path => (path.Relative, path.Absolute)));
    }

    /// <summary>
    ///     True when every document the pinned generation's current view holds for a changed file has the content the
    ///     disk holds. A file with no document in the view is not compared: its project is not bound yet. The writer
    ///     sync at the start of the check applied the changed files, so a disagreement here is a file that changed
    ///     after the pin or could not be read, and this answer is not kept.
    /// </summary>
    /// <returns>False when a changed file could not be read or still disagrees, so this answer is not kept.</returns>
    private async Task<bool> ViewsMatchDiskAsync(Generation generation, CancellationToken cancellationToken)
    {
        var stale = 0;
        foreach (var path in generation.Changed)
        {
            if (!await MatchesDiskAsync(generation.Current, path, cancellationToken).ConfigureAwait(false))
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
    ///     Makes a reached project no view holds fail the check instead of narrowing it without saying so. When the
    ///     pinned generation lacks one, it asks the writer to load it: the writer publishes a newer generation, and
    ///     this check restarts on it, so it never binds from a mix of the two. What is still missing after the writer
    ///     has had its round is reported, as is a load failure a background load recorded for a reached project.
    /// </summary>
    /// <returns>How each view held the reached projects before the writer round, or null when nothing was missing.</returns>
    /// <exception cref="FuseException">A reached project is still missing after the writer round, or failed to load.</exception>
    private async Task<string?> RecoverCoverageAsync(IReadOnlyList<ProjectNode> reached, Generation generation, CancellationToken cancellationToken)
    {
        var uncovered = Uncovered(reached, generation.Current, generation.Baseline);
        if (uncovered.Count == 0)
        {
            ThrowOnLoadFailure(reached);
            return null;
        }

        var held = HeldLine(reached, generation);
        var newer = await _workspace.EnsureLoadedAndPinAsync(uncovered, cancellationToken).ConfigureAwait(false);
        if (newer.Number == generation.Number)
            newer = await _workspace.RebuildAndPinAsync(cancellationToken).ConfigureAwait(false);
        if (newer.Number != generation.Number)
            throw new CoverageRestartException();
        var still = Uncovered(reached, newer.Current, newer.Baseline);
        if (still.Count > 0)
        {
            _workspace.Log($"check coverage: {held}");
            throw CoverageFailed(still[0]);
        }

        ThrowOnLoadFailure(reached);
        return held;
    }

    /// <summary>
    ///     Writes the coverage line for an affected check: one whose views missed a reached project, or whose precise reach
    ///     names a file on disk with no document in the current view. A check neither holds for logs nothing.
    /// </summary>
    private void LogCoverage(IReadOnlyList<ProjectNode> reached, string? coverage, Reach reach, Generation generation)
    {
        var missing = reach is Reach.Precise precise ? CandidatesWithoutDocument(precise, generation) : [];
        if (coverage is null && missing.Count == 0)
            return;
        var line = $"check coverage: {coverage ?? HeldLine(reached, generation)}";
        if (coverage is not null)
            line += "; recovered after rebuilding the views";
        if (missing.Count > 0)
            line += $"; candidates without a document: {string.Join(",", missing)}";
        _workspace.Log(line);
    }

    /// <summary>The relative paths of <paramref name="precise"/>'s candidates on disk with no document in the pinned view, in order.</summary>
    private List<string> CandidatesWithoutDocument(Reach.Precise precise, Generation generation)
    {
        var current = generation.Current;
        return [.. precise.Causes.Keys
            .Where(path => File.Exists(path.Absolute) && current.GetDocumentIdsWithFilePath(path.Absolute).IsEmpty)
            .Select(path => path.Relative)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Which reached projects each view holds a project for, in reached order, as the coverage line names them.</summary>
    private string HeldLine(IReadOnlyList<ProjectNode> reached, Generation generation)
    {
        var current = generation.Current;
        var baseline = generation.Baseline;
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
        // The log holds every project and file.
        return ", whole projects: " + string.Join("; ", causes.Select(WholeCause));
    }

    /// <summary>How many changed files each session wrote, most files first; empty when no session wrote any.</summary>
    private string SessionCounts(Generation generation)
    {
        var counts = _sessions.WritersOf(generation.Changed)
            .SelectMany(w => w.Value)
            .GroupBy(session => session, StringComparer.Ordinal)
            .Select(g => (Session: g.Key, Files: g.Count()))
            .OrderByDescending(c => c.Files)
            .ThenBy(c => c.Session, StringComparer.Ordinal)
            .ToList();
        if (counts.Count == 0)
            return "";
        var shown = counts.Take(MaxLoggedSessions).Select(c => $"{c.Session} {c.Files} file(s)").ToList();
        var more = counts.Count - shown.Count;
        return "; sessions: " + string.Join(", ", shown) + (more > 0 ? $", and {more} more" : "");
    }

    private static List<ProjectNode> OwnersOf(RepoGraph graph, IEnumerable<RepoPath> paths) =>
        paths.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();

    /// <summary>The writer published a newer generation that holds a project the pinned one lacked, so this attempt stops and the check restarts on it.</summary>
    private sealed class CoverageRestartException : Exception
    {
    }
}
