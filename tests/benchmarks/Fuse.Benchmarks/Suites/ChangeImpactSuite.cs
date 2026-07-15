using System.Diagnostics;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite B: PR/change impact (the flagship). For each pull request it materializes the head tree in a
///     git worktree, indexes it, and runs <c>fuse review</c> against the PR base, then scores the returned
///     context against two ground truths: the changed C# files, and (when the PR is adjudicated) the changed
///     files unioned with an adjudicated reading set of support files a reviewer must read. It also scores a
///     grep baseline at the same budgets, so review is measured against changed-files-only and against grep.
/// </summary>
/// <remarks>
///     Changed-file recall is high by construction (review seeds changed files as must-keep), so the
///     discriminating axes are precision (how tightly the blast radius is scoped), the adjudicated reading-set
///     recall (does review reach the support files), and tokens. Corpus-bound: when no repository in the
///     dataset is present on disk, the suite returns a skip result rather than failing.
/// </remarks>
public sealed class ChangeImpactSuite : IEvalSuite
{
    private const int DefaultBudget = 25_000;
    private readonly SemanticIndexer _indexer;
    private readonly IChangeSource _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ChangeImpactSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="changeSource">The git change source used to resolve the PR diff.</param>
    public ChangeImpactSuite(SemanticIndexer indexer, IChangeSource changeSource)
    {
        _indexer = indexer;
        _changeSource = changeSource;
    }

    /// <inheritdoc />
    public string Name => "review";

    /// <inheritdoc />
    public string Description => "Change impact: fuse review recall/precision/tokens over PR ground truth.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var budgets = options.Budgets is { Count: > 0 } ? options.Budgets : [DefaultBudget];
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1", options.DatasetFile, options.ManifestPath);
        var notes = new List<string> { $"token budgets {string.Join(", ", budgets.Select(b => b.ToString("N0")))}" };

        var present = dataset.Repos
            .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped. Run setup-corpus to populate the corpus.");
            return Skipped(notes);
        }

        var primary = new List<TaskResult>();
        var scores = new List<ReviewScore>();
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        var adjudicatedCount = 0;
        // T2: per-PR public-API delta section, recorded for the 10-PR hand adjudication gate. Non-empty only when a
        // PR changes the public or protected surface, so the list is the adjudication set itself.
        var apiDeltaLines = new List<string>();
        foreach (var repo in present)
        {
            var repoTasks = options.Limit > 0 ? repo.Tasks.Take(options.Limit).ToList() : repo.Tasks;
            foreach (var task in repoTasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (taskScores, result, mode, adjudicated) =
                    await ScoreTaskAsync(manager, repo, task, budgets, options, apiDeltaLines, cancellationToken);
                if (result is not null)
                    primary.Add(result);
                scores.AddRange(taskScores);
                if (adjudicated)
                    adjudicatedCount++;
                if (mode is not null)
                    modes[mode] = modes.GetValueOrDefault(mode) + 1;
            }
        }

        if (modes.Count > 0)
            notes.Add("index modes: " + string.Join(", ", modes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        notes.Add($"adjudicated PRs (with a reading set): {adjudicatedCount}");

        notes.Add($"api-delta PRs (non-empty public surface change): {apiDeltaLines.Count}");
        notes.AddRange(apiDeltaLines);

        if (primary.Count == 0)
        {
            notes.Add("No tasks could be scored (indexing or worktree failures).");
            return Skipped(notes);
        }

        AddComparisonNotes(scores, budgets, notes);
        return Aggregate(primary, notes);
    }

    private async Task<(IReadOnlyList<ReviewScore> Scores, TaskResult? Primary, string? Mode, bool Adjudicated)> ScoreTaskAsync(
        CorpusManager manager, RepoTasks repo, PrTask task, IReadOnlyList<int> budgets, EvalOptions options,
        List<string> apiDeltaLines, CancellationToken ct)
    {
        if (task.HeadRef is null || task.BaseRef is null)
            return ([], null, null, false);

        var worktree = await manager.AddWorktreeAsync(repo.Path!, task.HeadRef, ct);
        if (worktree is null)
        {
            options.Report($"review: {task.Id} worktree failed; skipped");
            return ([], null, null, false);
        }

        var changedTruth = task.GroundTruth.Files
            .Where(f => f.Role is "changed" or "test")
            .Select(f => Normalize(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adjudicatedTruth = task.GroundTruth.Files
            .Select(f => Normalize(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAdjudicated = adjudicatedTruth.Count > changedTruth.Count;

        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-b", Guid.NewGuid().ToString("N"), "fuse.db");
        string? mode = null;
        var scores = new List<ReviewScore>();
        TaskResult? primary = null;
        try
        {
            if (options.Restore)
                await manager.RestoreAsync(worktree, ct);

            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(ct);
            mode = (await _indexer.IndexAsync(worktree, store, ct)).Mode;
            if (options.RequireSemantic && mode != "semantic")
            {
                options.Report($"review: {task.Id} indexed as {mode}, not semantic; skipped under --require-semantic");
                return ([], null, mode, isAdjudicated);
            }

            var engine = new SemanticRetrievalEngine(store, _changeSource);

            // T2: the public-API delta for this PR (base ref versus the restored head worktree). Syntax-only, so it
            // does not need a semantic load; recorded for the hand adjudication gate. Best-effort - a git failure
            // leaves the line out rather than failing the scored task.
            try
            {
                var changedFiles = await _changeSource.GetChangedFilesAsync(worktree, task.BaseRef, ct);
                var delta = await ChangedApiSurfaceGatherer.GatherAsync(
                    _changeSource, worktree, task.BaseRef, changedFiles,
                    (relativePath, _) =>
                    {
                        var absolute = Path.Combine(worktree, relativePath);
                        return Task.FromResult(File.Exists(absolute) ? File.ReadAllText(absolute) : null);
                    },
                    ct);
                if (delta.Changes.Count > 0)
                {
                    var names = string.Join("; ", delta.Changes.Select(c => $"{(c.Breaking ? "BREAK " : "add ")}{c.Symbol}"));
                    apiDeltaLines.Add($"apidelta {task.Id}: {delta.Breaking.Count} breaking, {delta.Changes.Count - delta.Breaking.Count} additive :: {names}");
                }
            }
            catch (ChangeSourceException)
            {
                // git base unavailable in this worktree; the api-delta is simply not recorded for this PR.
            }

            for (var i = 0; i < budgets.Count; i++)
            {
                var budget = budgets[i];
                var stopwatch = Stopwatch.StartNew();
                var plan = await engine.ReviewAsync(new ReviewRequest(worktree, task.BaseRef, MaxTokens: budget), ct);
                stopwatch.Stop();

                var retrieved = plan.Items
                    .Select(item => Normalize(item.Path))
                    .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var grep = await GrepBaselineAsync(worktree, store, task.Title, budget, ct);

                scores.Add(new ReviewScore(
                    budget,
                    Metrics.Recall(retrieved, changedTruth),
                    Metrics.Precision(retrieved.ToList(), changedTruth),
                    plan.EstimatedTokens,
                    isAdjudicated ? Metrics.Recall(retrieved, adjudicatedTruth) : null,
                    isAdjudicated ? Metrics.Precision(retrieved.ToList(), adjudicatedTruth) : null,
                    Metrics.Recall(grep, changedTruth),
                    Metrics.Precision(grep.ToList(), changedTruth)));

                // The primary scorecard (review.json) is fuse-vs-changed at the first budget, unchanged.
                if (i == 0)
                {
                    options.Report($"review: {task.Id} recall {Metrics.Recall(retrieved, changedTruth):P0} precision {Metrics.Precision(retrieved.ToList(), changedTruth):P0} tokens {plan.EstimatedTokens}");
                    primary = new TaskResult(
                        task.Id, task.Repo, task.Category,
                        Metrics.Recall(retrieved, changedTruth), Metrics.Precision(retrieved.ToList(), changedTruth),
                        plan.EstimatedTokens, stopwatch.ElapsedMilliseconds,
                        new TaskFiles(
                            changedTruth.Where(retrieved.Contains).ToList(),
                            changedTruth.Where(g => !retrieved.Contains(g)).ToList(),
                            retrieved.Where(r => !adjudicatedTruth.Contains(r)).Take(20).ToList()));
                }
            }

            return (scores, primary, mode, isAdjudicated);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            options.Report($"review: {task.Id} failed: {e.Message}");
            return (scores, primary, mode, isAdjudicated);
        }
        finally
        {
            TryDeleteStore(databasePath);
            await manager.RemoveWorktreeAsync(repo.Path!, worktree, ct);
        }
    }

    // A grep baseline: rank the worktree's C# files by how many of the title's identifier tokens they contain,
    // then admit files in rank order until their estimated token cost would exceed the budget. This is the
    // naive "search the title across the repo" alternative that review is measured against.
    private static async Task<HashSet<string>> GrepBaselineAsync(
        string worktree, IWorkspaceIndexStore store, string title, int budget, CancellationToken ct)
    {
        var tokens = title
            .Split([' ', '\t', '\n', '.', '(', ')', ',', '/', ':', '#', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && t.Any(char.IsLetter))
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0)
            return result;

        var matches = new List<(string Path, int Count)>();
        foreach (var file in Directory.EnumerateFiles(worktree, "*.cs", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Normalize(Path.GetRelativePath(worktree, file));
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                continue;
            string content;
            try
            {
                content = (await File.ReadAllTextAsync(file, ct)).ToLowerInvariant();
            }
            catch (IOException)
            {
                continue;
            }

            var count = tokens.Sum(t => CountOccurrences(content, t));
            if (count > 0)
                matches.Add((normalized, count));
        }

        var spent = 0;
        foreach (var (path, _) in matches.OrderByDescending(m => m.Count).ThenBy(m => m.Path, StringComparer.Ordinal))
        {
            var cost = await store.GetFileTokenEstimateAsync(path, ct);
            if (cost == 0)
                cost = 200; // a file the index did not estimate still costs something to read.
            if (spent + cost > budget && result.Count > 0)
                break;
            result.Add(path);
            spent += cost;
        }

        return result;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void AddComparisonNotes(IReadOnlyList<ReviewScore> scores, IReadOnlyList<int> budgets, List<string> notes)
    {
        foreach (var budget in budgets)
        {
            var atBudget = scores.Where(s => s.Budget == budget).ToList();
            if (atBudget.Count == 0)
                continue;

            var fuseRecall = Metrics.Mean(atBudget.Select(s => s.FuseChangedRecall).ToList());
            var fusePrecision = Metrics.Mean(atBudget.Select(s => s.FuseChangedPrecision).ToList());
            var medianTokens = Metrics.Median(atBudget.Select(s => (double)s.FuseTokens));
            notes.Add($"budget {budget:N0} fuse vs changed: recall {fuseRecall:P0}, precision {fusePrecision:P0}, median tokens {medianTokens:N0}");

            var grepRecall = Metrics.Mean(atBudget.Select(s => s.GrepChangedRecall).ToList());
            var grepPrecision = Metrics.Mean(atBudget.Select(s => s.GrepChangedPrecision).ToList());
            notes.Add($"budget {budget:N0} grep vs changed: recall {grepRecall:P0}, precision {grepPrecision:P0}");

            var adj = atBudget.Where(s => s.FuseAdjudicatedRecall is not null).ToList();
            if (adj.Count > 0)
            {
                var adjRecall = Metrics.Mean(adj.Select(s => s.FuseAdjudicatedRecall!.Value).ToList());
                var adjPrecision = Metrics.Mean(adj.Select(s => s.FuseAdjudicatedPrecision!.Value).ToList());
                notes.Add($"budget {budget:N0} fuse vs adjudicated (n {adj.Count}): recall {adjRecall:P0}, precision {adjPrecision:P0}");
            }
        }
    }

    private SuiteResult Aggregate(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        var recalls = tasks.Select(t => t.Recall).ToList();
        var precisions = tasks.Select(t => t.Precision).ToList();
        var tokens = tasks.Select(t => (double)t.Tokens).ToList();
        var (ciLow, ciHigh) = Metrics.BootstrapCi(recalls);
        var meanRecall = Metrics.Mean(recalls);
        var meanPrecision = Metrics.Mean(precisions);

        return new SuiteResult(Name, Description, null,
            new Scorecard(
                tasks.Count, meanRecall, ciLow, ciHigh, meanPrecision,
                Metrics.F1(meanPrecision, meanRecall),
                Metrics.Median(tokens), Metrics.Mean(tokens), 0),
            tasks, notes);
    }

    private SuiteResult Skipped(IReadOnlyList<string> notes)
        => new(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void TryDeleteStore(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            SqliteConnection.ClearAllPools();
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // One scored PR at one budget: fuse review against the changed-files truth and (when adjudicated) against
    // the changed-plus-reading-set truth, and the grep baseline against the changed-files truth.
    private readonly record struct ReviewScore(
        int Budget,
        double FuseChangedRecall,
        double FuseChangedPrecision,
        int FuseTokens,
        double? FuseAdjudicatedRecall,
        double? FuseAdjudicatedPrecision,
        double GrepChangedRecall,
        double GrepChangedPrecision);
}
