using System.Diagnostics;
using System.Text;
using Fuse.Check.Candidates;
using Fuse.Check.Diagnostics;
using Fuse.Check.Enrichment;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Judge.Model;
using Fuse.Paths;
using Fuse.Workspace.Generations;
namespace Fuse.Judge;

/// <summary>
///     Scores candidate unified diffs in memory. Each candidate is applied to a fork of the current solution without
///     writing disk, the fork is checked for the errors it introduces compared with the baseline, and the candidates
///     are ranked (race) or checked applied together with overlaps reported (combine).
/// </summary>
internal sealed class CandidateJudge
{
    /// <summary>How many introduced errors one verdict holds at most; the count itself is not capped.</summary>
    private const int MaxReportedErrors = 200;

    private readonly RepoWorkspace _workspace;
    private readonly RepoRoot _root;

    /// <param name="workspace">The repository's projects and views.</param>
    /// <param name="root">The repository, which turns a diff's paths into values.</param>
    public CandidateJudge(RepoWorkspace workspace, RepoRoot root)
    {
        _workspace = workspace;
        _root = root;
    }

    /// <param name="candidates">The candidates to score, with the names their verdicts are ranked under.</param>
    /// <param name="mode">Whether to race or combine the candidates.</param>
    /// <param name="budget">How many candidates the call scores and how many bytes of diff it holds.</param>
    /// <param name="cancellationToken">Cancels the scoring.</param>
    public async Task<JudgeVerdict> RunAsync(IReadOnlyList<JudgeCandidate> candidates, JudgeMode mode, JudgeBudget budget, CancellationToken cancellationToken)
    {
        ThrowWhenPastBounds(candidates, budget);
        var prepared = candidates.Select(Prepare).ToList();
        var touched = prepared.SelectMany(p => p.Files).SelectMany(f => f.Target is { } target ? new[] { f.Path, target } : new[] { f.Path }).Distinct().ToList();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        generation = await EnsureTouchLoadedAsync(generation, touched, cancellationToken).ConfigureAwait(false);
        var bases = await ForkChecker.ReadBasesAsync(generation, touched, cancellationToken).ConfigureAwait(false);
        var introduced = new IntroducedErrors(_workspace);
        var reacher = new ChangeReach(_workspace);
        var binding = new CandidateBinding(_workspace, introduced);
        try
        {
            return mode == JudgeMode.Combine
                ? await CombineAsync(prepared, generation, bases, introduced, reacher, binding, cancellationToken).ConfigureAwait(false)
                : await RaceAsync(prepared, generation, bases, introduced, reacher, binding, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            introduced.ReleaseBaseline();
        }
    }

    /// <summary>Scores each candidate on its own fork and ranks the verdicts.</summary>
    private async Task<JudgeVerdict> RaceAsync(
        IReadOnlyList<PreparedCandidate> prepared,
        Generation generation,
        IReadOnlyDictionary<RepoPath, string?> bases,
        IntroducedErrors introduced,
        ChangeReach reacher,
        CandidateBinding binding,
        CancellationToken cancellationToken)
    {
        var verdicts = new CandidateScore[prepared.Count];
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
        return new JudgeVerdict("race", clean, RaceSummary(prepared.Count, ranked, winner), winner?.Name, [.. ranked], []);
    }

    /// <summary>Scores one candidate: its fork's introduced errors, or why its patch does not apply.</summary>
    private async Task<CandidateScore> RaceOneAsync(
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
        var (fork, touched) = await ForkChecker.ForkAsync(generation.Current, generation.Graph, buffers, cancellationToken).ConfigureAwait(false);
        var owned = touched.Where(p => generation.Graph.OwnersOf(p).Count > 0).ToList();
        var (errors, reach, breaks) = await ForkChecker.CheckForkAsync(fork, generation, owned, introduced, reacher, binding, cancellationToken).ConfigureAwait(false);
        var ordered = errors.Distinct().OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line).ThenBy(e => e.Column).ToList();
        var reported = CauseLines.Attach(ordered, reach, _root, owned).Take(MaxReportedErrors).Select(Score).ToArray();
        return new CandidateScore(
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
    private async Task<JudgeVerdict> CombineAsync(
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
        var conflictByIndex = new Dictionary<int, CandidateScore>();
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

        var (fork, touched) = await ForkChecker.ForkAsync(generation.Current, generation.Graph, buffers, cancellationToken).ConfigureAwait(false);
        var owned = touched.Where(p => generation.Graph.OwnersOf(p).Count > 0).ToList();
        var (errors, reach, breaks) = await ForkChecker.CheckForkAsync(fork, generation, owned, introduced, reacher, binding, cancellationToken).ConfigureAwait(false);
        var ordered = errors.Distinct().OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line).ThenBy(e => e.Column).ToList();
        var reported = CauseLines.Attach(ordered, reach, _root, owned).Take(MaxReportedErrors).Select(Score).ToArray();
        var ranked = new List<CandidateScore>(prepared.Count);
        for (var i = 0; i < prepared.Count; i++)
        {
            if (conflictByIndex.TryGetValue(i, out var conflicted))
            {
                ranked.Add(conflicted with { Rank = i + 1 });
                continue;
            }

            ranked.Add(new CandidateScore(
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
        return new JudgeVerdict("combine", clean, CombineSummary(prepared.Count, ordered.Count, conflicts.Length), null, [.. ranked], conflicts);
    }

    /// <summary>Parses one candidate diff into the source files it touches, or the reason it cannot be scored.</summary>
    private PreparedCandidate Prepare(JudgeCandidate candidate)
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
    private static void ThrowWhenPastBounds(IReadOnlyList<JudgeCandidate> candidates, JudgeBudget budget)
    {
        if (candidates.Count > budget.MaxCandidates)
            throw new FuseException(ErrorCode.InvalidPath, $"the {candidates.Count} candidates named are more than the {budget.MaxCandidates} a judge call scores; name fewer");
        long totalBytes = 0;
        foreach (var candidate in candidates)
        {
            var bytes = Encoding.UTF8.GetByteCount(candidate.Diff);
            if (bytes > budget.MaxDiffBytes)
                throw new FuseException(ErrorCode.InvalidPath, $"the diff of '{candidate.Name}' holds {bytes} bytes, more than the 1 MB a candidate holds; shorten the diff");
            totalBytes += bytes;
            if (totalBytes > budget.MaxTotalDiffBytes)
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
                var created = UnifiedDiff.ApplyToText("", patch);
                switch (created)
                {
                    case PatchResult.Conflicted conflicted:
                        return conflicted.Reason;
                    case PatchResult.Applied applied:
                        buffers[file.Path] = applied.Text;
                        break;
                    default:
                        throw new UnreachableException($"a patch result is applied or conflicted, not {created.GetType().Name}");
                }

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
                var renamed = UnifiedDiff.ApplyToText(current, patch);
                switch (renamed)
                {
                    case PatchResult.Conflicted conflicted:
                        return conflicted.Reason;
                    case PatchResult.Applied applied:
                        buffers[file.Path] = null;
                        buffers[target] = applied.Text;
                        break;
                    default:
                        throw new UnreachableException($"a patch result is applied or conflicted, not {renamed.GetType().Name}");
                }

                continue;
            }

            if (patch.IsDeleted)
            {
                if (UnifiedDiff.ApplyToText(current, patch) is PatchResult.Conflicted deleteConflict)
                    return deleteConflict.Reason;
                buffers[file.Path] = null;
                continue;
            }

            var appliedText = UnifiedDiff.ApplyToText(current, patch);
            switch (appliedText)
            {
                case PatchResult.Conflicted conflicted:
                    return conflicted.Reason;
                case PatchResult.Applied applied:
                    buffers[file.Path] = applied.Text;
                    break;
                default:
                    throw new UnreachableException($"a patch result is applied or conflicted, not {appliedText.GetType().Name}");
            }
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

    /// <summary>The verdict for a candidate whose patch does not apply.</summary>
    private static CandidateScore Conflicted(string name, string reason, Stopwatch timer) =>
        new(name, Rank: 0, Compiles: false, ErrorCount: 0, ConflictReason: reason) { ElapsedMs = timer.ElapsedMilliseconds };

    /// <summary>One introduced error as the judge scores it, with the cause to print under it.</summary>
    private static CandidateScore.ScoredError Score(IntroducedError error)
    {
        var cause = error.Cause;
        CandidateScore.ScoredCause? scored = cause switch
        {
            Cause.Changed changed => new CandidateScore.ScoredCause(Removed: false, changed.Declaration),
            Cause.Removed removed => new CandidateScore.ScoredCause(Removed: true, removed.Declaration),
            null => null,
            _ => throw new UnreachableException($"a cause is changed or removed, not {cause.GetType().Name}"),
        };
        return new CandidateScore.ScoredError(
            error.Error.Path,
            error.Error.Line,
            error.Error.Column,
            error.Error.Id,
            error.Error.Message,
            error.Error.FromAnalyzer,
            scored,
            error.IsCauseLeftOut)
        {
            GeneratedFile = error.GeneratedFile,
            CausedBy = error.CausedBy,
        };
    }

    /// <summary>The one-line verdict over a race.</summary>
    private static string RaceSummary(int count, List<CandidateScore> ranked, CandidateScore? winner)
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
    private sealed record PreparedCandidate(JudgeCandidate Spec, IReadOnlyList<PatchedFile> Files, string? Conflict);

    /// <summary>One file patch with its paths resolved: the file it reads and, for a rename, the file it writes.</summary>
    private sealed record PatchedFile(UnifiedDiff.FilePatch Patch, RepoPath Path, RepoPath? Target);

    /// <summary>Old-file lines one candidate's hunks hold, half open, so a later candidate that touches them conflicts.</summary>
    private readonly record struct ClaimedRange(int Start, int End, string Owner);
}
