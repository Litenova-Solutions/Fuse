using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Check.Candidates;
using Fuse.Check.Diagnostics;
using Fuse.Check.Enrichment;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Requests;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Engine;

/// <summary>
///     Scores candidate unified diffs in memory. Each candidate is applied to a fork of the current solution without
///     writing disk, the fork is checked for the errors it introduces compared with the baseline, and the candidates
///     are ranked (race) or checked applied together with overlaps reported (combine).
/// </summary>
internal sealed class CandidateJudge
{
    /// <summary>How many introduced errors one verdict holds at most; the count itself is not capped.</summary>
    private const int MaxReportedErrors = 200;

    /// <summary>How many candidates one judge call scores; past it the call is refused before anything is parsed.</summary>
    private const int MaxCandidates = 16;

    /// <summary>How many bytes of diff one candidate holds; past it the call is refused before anything is parsed.</summary>
    private const int MaxDiffBytes = 1024 * 1024;

    /// <summary>How many bytes of diff one judge call scores in total; past it the call is refused before anything is parsed.</summary>
    private const int MaxTotalDiffBytes = 8 * 1024 * 1024;

    private readonly RepoWorkspace _workspace;
    private readonly RepoRoot _root;

    /// <param name="workspace">The repository's projects and views.</param>
    /// <param name="root">The repository, which turns a diff's paths into values.</param>
    public CandidateJudge(RepoWorkspace workspace, RepoRoot root)
    {
        _workspace = workspace;
        _root = root;
    }

    /// <param name="spec">The candidates and whether to race or combine them.</param>
    /// <param name="cancellationToken">Cancels the scoring.</param>
    public async Task<JudgeReport> RunAsync(JudgeRequest spec, CancellationToken cancellationToken)
    {
        ThrowWhenPastBounds(spec);
        var prepared = spec.Candidates.Select(Prepare).ToList();
        var touched = prepared.SelectMany(p => p.Files).SelectMany(f => f.Target is { } target ? new[] { f.Path, target } : new[] { f.Path }).Distinct().ToList();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        generation = await EnsureTouchLoadedAsync(generation, touched, cancellationToken).ConfigureAwait(false);
        var bases = await ReadBasesAsync(generation, touched, cancellationToken).ConfigureAwait(false);
        var introduced = new IntroducedErrors(_workspace);
        var reacher = new ChangeReach(_workspace);
        var binding = new CandidateBinding(_workspace, introduced);
        try
        {
            return spec.Mode == JudgeRequest.JudgeMode.Combine
                ? await CombineAsync(prepared, generation, bases, introduced, reacher, binding, cancellationToken).ConfigureAwait(false)
                : await RaceAsync(prepared, generation, bases, introduced, reacher, binding, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            introduced.ReleaseBaseline();
        }
    }

    /// <summary>Scores each candidate on its own fork and ranks the verdicts.</summary>
    private async Task<JudgeReport> RaceAsync(
        IReadOnlyList<PreparedCandidate> prepared,
        Generation generation,
        IReadOnlyDictionary<RepoPath, string?> bases,
        IntroducedErrors introduced,
        ChangeReach reacher,
        CandidateBinding binding,
        CancellationToken cancellationToken)
    {
        var verdicts = new JudgeReport.CandidateVerdict[prepared.Count];
        await Parallel.ForEachAsync(
            prepared.Select((candidate, index) => (candidate, index)),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                verdicts[item.index] = await RaceOneAsync(item.candidate, generation, bases, introduced, reacher, binding, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var ranked = verdicts
            .OrderBy(v => v.ConflictReason is not null)
            .ThenBy(v => v.Compiles ? 0 : 1)
            .ThenBy(v => v.ErrorCount)
            .ThenBy(v => v.SurfaceBreaks)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .Select((verdict, index) => verdict with { Rank = index + 1 })
            .ToList();
        var winner = ranked.FirstOrDefault(v => v.ConflictReason is null);
        var clean = ranked.Count == 0 || (winner is not null && winner.ErrorCount == 0);
        _workspace.Log($"judge: race scored {prepared.Count} candidate(s), winner {(winner is null ? "none" : winner.Name)}");
        return new JudgeReport("race", clean, RaceSummary(prepared.Count, ranked, winner), winner?.Name, [.. ranked], []);
    }

    /// <summary>Scores one candidate: its fork's introduced errors, or why its patch does not apply.</summary>
    private async Task<JudgeReport.CandidateVerdict> RaceOneAsync(
        PreparedCandidate prepared,
        Generation generation,
        IReadOnlyDictionary<RepoPath, string?> bases,
        IntroducedErrors introduced,
        ChangeReach reacher,
        CandidateBinding binding,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        if (prepared.Conflict is not null)
            return Conflicted(prepared.Spec.Name, prepared.Conflict, timer);
        var buffers = new Dictionary<RepoPath, string?>(bases);
        if (TryApply(prepared, buffers) is { } conflict)
            return Conflicted(prepared.Spec.Name, conflict, timer);
        var (fork, touched) = await ForkAsync(generation.Current, generation.Graph, buffers, cancellationToken).ConfigureAwait(false);
        var owned = touched.Where(p => generation.Graph.OwnersOf(p).Count > 0).ToList();
        var (errors, reach, breaks) = await CheckForkAsync(fork, generation, owned, introduced, reacher, binding, cancellationToken).ConfigureAwait(false);
        var ordered = errors.Distinct().OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line).ThenBy(e => e.Column).ToList();
        var reported = CauseLines.Attach(ordered, reach, _root, owned).Take(MaxReportedErrors).Select(Report).ToArray();
        return new JudgeReport.CandidateVerdict(
            prepared.Spec.Name,
            Rank: 0,
            Compiles: ordered.Count == 0,
            ErrorCount: ordered.Count,
            AnalyzerErrorCount: ordered.Count(e => e.FromAnalyzer),
            SurfaceBreaks: breaks,
            ElapsedMs: timer.ElapsedMilliseconds,
            Errors: reported);
    }

    /// <summary>Applies every candidate to one fork in order, reporting overlaps, and checks the fork once.</summary>
    private async Task<JudgeReport> CombineAsync(
        IReadOnlyList<PreparedCandidate> prepared,
        Generation generation,
        IReadOnlyDictionary<RepoPath, string?> bases,
        IntroducedErrors introduced,
        ChangeReach reacher,
        CandidateBinding binding,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var buffers = new Dictionary<RepoPath, string?>(bases);
        var claims = new Dictionary<RepoPath, List<ClaimedRange>>();
        var creators = new Dictionary<RepoPath, string>();
        var conflictByIndex = new Dictionary<int, JudgeReport.CandidateVerdict>();
        for (var i = 0; i < prepared.Count; i++)
        {
            var conflict = prepared[i].Conflict ?? FindOverlap(prepared[i], claims, creators);
            if (conflict is null)
            {
                var trial = new Dictionary<RepoPath, string?>(buffers);
                conflict = TryApply(prepared[i], trial);
                if (conflict is null)
                {
                    buffers = trial;
                    ClaimRanges(prepared[i], claims, creators);
                }
            }

            if (conflict is not null)
                conflictByIndex[i] = Conflicted(prepared[i].Spec.Name, conflict, timer);
        }

        var (fork, touched) = await ForkAsync(generation.Current, generation.Graph, buffers, cancellationToken).ConfigureAwait(false);
        var owned = touched.Where(p => generation.Graph.OwnersOf(p).Count > 0).ToList();
        var (errors, reach, breaks) = await CheckForkAsync(fork, generation, owned, introduced, reacher, binding, cancellationToken).ConfigureAwait(false);
        var ordered = errors.Distinct().OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line).ThenBy(e => e.Column).ToList();
        var reported = CauseLines.Attach(ordered, reach, _root, owned).Take(MaxReportedErrors).Select(Report).ToArray();
        var ranked = new List<JudgeReport.CandidateVerdict>(prepared.Count);
        for (var i = 0; i < prepared.Count; i++)
        {
            if (conflictByIndex.TryGetValue(i, out var conflicted))
            {
                ranked.Add(conflicted with { Rank = i + 1 });
                continue;
            }

            ranked.Add(new JudgeReport.CandidateVerdict(
                prepared[i].Spec.Name,
                Rank: i + 1,
                Compiles: ordered.Count == 0,
                ErrorCount: ordered.Count,
                AnalyzerErrorCount: ordered.Count(e => e.FromAnalyzer),
                SurfaceBreaks: breaks,
                ElapsedMs: timer.ElapsedMilliseconds,
                Errors: reported));
        }

        var conflicts = ranked.Where(v => v.ConflictReason is not null).Select(v => $"{v.Name}: {v.ConflictReason}").ToArray();
        var clean = ordered.Count == 0 && conflicts.Length == 0;
        _workspace.Log($"judge: combine scored {prepared.Count} candidate(s), {conflicts.Length} conflict(s)");
        return new JudgeReport("combine", clean, CombineSummary(prepared.Count, ordered.Count, conflicts.Length), null, [.. ranked], conflicts);
    }

    /// <summary>Checks one fork for the errors it introduces compared with the baseline, with the checker's own steps.</summary>
    /// <returns>The introduced errors, the reach they were found with, and how many declaration changes the fork holds.</returns>
    private static async Task<(List<CompilerError> Errors, Reach Reach, int Breaks)> CheckForkAsync(
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

    /// <summary>Parses one candidate diff into the source files it touches, or the reason it cannot be scored.</summary>
    private PreparedCandidate Prepare(JudgeRequest.Candidate candidate)
    {
        var (patches, parseError) = UnifiedDiff.Parse(candidate.Diff);
        if (parseError is not null)
            return new PreparedCandidate(candidate, [], parseError);
        var files = new List<PatchedFile>();
        foreach (var patch in patches)
        {
            if (!PathRules.IsSource(patch.Path))
                continue;
            RepoPath path;
            RepoPath? target;
            try
            {
                path = _root.PathOf(patch.Path);
                target = !patch.IsNew && !patch.IsDeleted && patch.NewPath != patch.OldPath ? _root.PathOf(patch.NewPath) : null;
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                return new PreparedCandidate(candidate, [], $"'{patch.Path}' is not a valid path: {e.Message}");
            }

            if (!_root.Contains(path.Absolute) || (target is not null && !_root.Contains(target.Value.Absolute)))
                continue;
            files.Add(new PatchedFile(patch, path, target));
        }

        // A candidate with no scorable file would check an unchanged fork and win with no errors; it conflicts instead.
        if (files.Count == 0)
            return new PreparedCandidate(candidate, [], "the diff touches no source file in this repository");
        return new PreparedCandidate(candidate, files, null);
    }

    /// <summary>Refuses a request past the candidate bounds, before anything is parsed.</summary>
    /// <exception cref="FuseException">The request names too many candidates, or too many bytes of diff.</exception>
    private static void ThrowWhenPastBounds(JudgeRequest spec)
    {
        if (spec.Candidates.Length > MaxCandidates)
            throw new FuseException(ErrorCode.InvalidPath, $"the {spec.Candidates.Length} candidates named are more than the {MaxCandidates} a judge call scores; name fewer");
        long totalBytes = 0;
        foreach (var candidate in spec.Candidates)
        {
            var bytes = Encoding.UTF8.GetByteCount(candidate.Diff);
            if (bytes > MaxDiffBytes)
                throw new FuseException(ErrorCode.InvalidPath, $"the diff of '{candidate.Name}' holds {bytes} bytes, more than the 1 MB a candidate holds; shorten the diff");
            totalBytes += bytes;
            if (totalBytes > MaxTotalDiffBytes)
                throw new FuseException(ErrorCode.InvalidPath, $"the candidates hold {totalBytes} bytes of diff, more than the 8 MB a judge call scores; name fewer or shorten the diffs");
        }
    }

    /// <summary>Pins a generation with the touched files' owners and their dependents loaded, so every fork binds from it.</summary>
    private async Task<Generation> EnsureTouchLoadedAsync(Generation generation, IReadOnlyList<RepoPath> touched, CancellationToken cancellationToken)
    {
        var owners = touched.SelectMany(generation.Graph.OwnersOf).DistinctBy(n => n.Path).ToList();
        if (owners.Count == 0)
            return generation;
        var dependents = owners.SelectMany(generation.Graph.DependentsOf).DistinctBy(n => n.Path).Where(n => !owners.Any(o => o.Path == n.Path)).ToList();
        return await _workspace.EnsureLoadedAndPinAsync(owners.Concat(dependents), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Each touched file's text in the current view, or in the baseline when the current view does not hold it.</summary>
    private static async Task<Dictionary<RepoPath, string?>> ReadBasesAsync(Generation generation, IReadOnlyList<RepoPath> touched, CancellationToken cancellationToken)
    {
        var bases = new Dictionary<RepoPath, string?>();
        foreach (var path in touched)
        {
            bases[path] = await DocumentTextAsync(generation.Current, path, cancellationToken).ConfigureAwait(false)
                ?? await DocumentTextAsync(generation.Baseline, path, cancellationToken).ConfigureAwait(false);
        }

        return bases;
    }

    /// <summary>The text of the file's document in the view, or null when the view holds no document for it.</summary>
    private static async Task<string?> DocumentTextAsync(Solution view, RepoPath path, CancellationToken cancellationToken)
    {
        var id = view.GetDocumentIdsWithFilePath(path.Absolute).FirstOrDefault();
        TextDocument? document = id is null ? null : view.GetDocument(id) ?? (TextDocument?)view.GetAdditionalDocument(id);
        return document is null ? null : (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
    }

    /// <summary>Applies one candidate's patches to the buffers, or names why they do not apply.</summary>
    /// <returns>The conflict reason, or null when every patch applied.</returns>
    private static string? TryApply(PreparedCandidate prepared, Dictionary<RepoPath, string?> buffers)
    {
        foreach (var file in prepared.Files)
        {
            var patch = file.Patch;
            var current = buffers[file.Path];
            if (patch.IsNew)
            {
                if (current is not null)
                    return $"{file.Path.Relative} is created by the patch but is already in the tree";
                var (created, createError) = UnifiedDiff.ApplyToText("", patch);
                if (createError is not null)
                    return createError;
                buffers[file.Path] = created;
                continue;
            }

            if (current is null)
            {
                if (patch.IsDeleted)
                    continue;
                return $"{file.Path.Relative} is changed by the patch but is not in the tree";
            }

            if (file.Target is { } target)
            {
                var (renamed, renameError) = UnifiedDiff.ApplyToText(current, patch);
                if (renameError is not null)
                    return renameError;
                buffers[file.Path] = null;
                buffers[target] = renamed;
                continue;
            }

            if (patch.IsDeleted)
            {
                if (UnifiedDiff.ApplyToText(current, patch).Error is { } deleteError)
                    return deleteError;
                buffers[file.Path] = null;
                continue;
            }

            var (text, error) = UnifiedDiff.ApplyToText(current, patch);
            if (error is not null)
                return error;
            buffers[file.Path] = text;
        }

        return null;
    }

    /// <summary>Names the earlier candidate the candidate's hunks overlap, or null when they touch nothing claimed.</summary>
    private static string? FindOverlap(PreparedCandidate candidate, IReadOnlyDictionary<RepoPath, List<ClaimedRange>> claims, Dictionary<RepoPath, string> creators)
    {
        foreach (var file in candidate.Files)
        {
            if (creators.TryGetValue(file.Path, out var creator))
                return $"hunks overlap {creator} in {file.Path.Relative}";
            if (file.Target is { } target && creators.TryGetValue(target, out var targetCreator))
                return $"hunks overlap {targetCreator} in {target.Relative}";
            if (!claims.TryGetValue(file.Path, out var ranges))
                continue;
            foreach (var hunk in file.Patch.Hunks)
            {
                var start = hunk.OldStart;
                var end = hunk.OldStart + Math.Max(hunk.OldCount, 1);
                foreach (var range in ranges)
                {
                    if (start < range.End && range.Start < end)
                        return $"hunks overlap {range.Owner} in {file.Path.Relative}";
                }
            }
        }

        return null;
    }

    /// <summary>Claims the candidate's hunk ranges and created files, so a later candidate that touches them conflicts.</summary>
    private static void ClaimRanges(PreparedCandidate candidate, Dictionary<RepoPath, List<ClaimedRange>> claims, Dictionary<RepoPath, string> creators)
    {
        foreach (var file in candidate.Files)
        {
            if (file.Target is { } target)
                creators[target] = candidate.Spec.Name;
            if (file.Patch.IsNew)
            {
                creators[file.Path] = candidate.Spec.Name;
                continue;
            }

            if (!claims.TryGetValue(file.Path, out var ranges))
                claims[file.Path] = ranges = [];
            if (file.Patch.IsDeleted)
            {
                ranges.Add(new ClaimedRange(0, int.MaxValue, candidate.Spec.Name));
                continue;
            }

            foreach (var hunk in file.Patch.Hunks)
                ranges.Add(new ClaimedRange(hunk.OldStart, hunk.OldStart + Math.Max(hunk.OldCount, 1), candidate.Spec.Name));
        }
    }

    /// <summary>The current view with the buffers' texts, and the files that differ from it.</summary>
    private static async Task<(Solution Fork, List<RepoPath> Touched)> ForkAsync(Solution current, RepoGraph graph, IReadOnlyDictionary<RepoPath, string?> buffers, CancellationToken cancellationToken)
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

    /// <summary>The verdict for a candidate whose patch does not apply.</summary>
    private static JudgeReport.CandidateVerdict Conflicted(string name, string reason, Stopwatch timer) =>
        new(name, Rank: 0, Compiles: false, ErrorCount: 0, ConflictReason: reason) { ElapsedMs = timer.ElapsedMilliseconds };

    /// <summary>One introduced error as the client receives it.</summary>
    private static ReportedError Report(IntroducedError error) =>
        new(error.Error, error.Cause switch
        {
            Cause.Changed changed => new ReportedCause(CauseKind.Changed, changed.Declaration),
            Cause.Removed removed => new ReportedCause(CauseKind.Removed, removed.Declaration),
            null => null,
            _ => throw new UnreachableException($"a cause is changed or removed, not {error.Cause!.GetType().Name}"),
        });

    /// <summary>The one-line verdict over a race.</summary>
    private static string RaceSummary(int count, List<JudgeReport.CandidateVerdict> ranked, JudgeReport.CandidateVerdict? winner)
    {
        string summary;
        if (ranked.Count == 0)
        {
            summary = "no candidates to judge";
        }
        else if (winner is null)
        {
            summary = $"no candidate applied: {ConflictsPhrase(count)}";
        }
        else
        {
            summary = winner.ErrorCount == 0
                ? $"best of {count}: {winner.Name} compiles with no errors introduced"
                : $"best of {count}: {winner.Name} introduces {ErrorsPhrase(winner.ErrorCount)}";
            var conflicts = ranked.Count(v => v.ConflictReason is not null);
            if (conflicts > 0)
                summary += $", {ConflictsPhrase(conflicts)}";
        }

        return summary;
    }

    /// <summary>The one-line verdict over a combine.</summary>
    private static string CombineSummary(int count, int errors, int conflicts)
    {
        if (count == 0)
            return "no candidates to judge";
        var summary = errors == 0
            ? $"combined {count - conflicts} of {count} with no errors introduced"
            : $"combined {count - conflicts} of {count} introduces {ErrorsPhrase(errors)}";
        if (conflicts > 0)
            summary += $", {ConflictsPhrase(conflicts)}";
        return summary;
    }

    private static string ErrorsPhrase(int count) => count == 1 ? "1 error" : $"{count} errors";

    private static string ConflictsPhrase(int count) => count == 1 ? "1 conflict" : $"{count} conflicts";

    /// <summary>One candidate with its diff parsed: the source files it touches, or why it cannot be scored.</summary>
    private sealed record PreparedCandidate(JudgeRequest.Candidate Spec, IReadOnlyList<PatchedFile> Files, string? Conflict);

    /// <summary>One file patch with its paths resolved: the file it reads and, for a rename, the file it writes.</summary>
    private sealed record PatchedFile(UnifiedDiff.FilePatch Patch, RepoPath Path, RepoPath? Target);

    /// <summary>Old-file lines one candidate's hunks hold, half open, so a later candidate that touches them conflicts.</summary>
    private readonly record struct ClaimedRange(int Start, int End, string Owner);
}
