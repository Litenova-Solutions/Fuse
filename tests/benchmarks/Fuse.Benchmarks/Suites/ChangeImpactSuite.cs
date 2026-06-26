using System.Diagnostics;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite B: PR/change impact (the flagship). For each pull request it materializes the head tree in a
///     git worktree, indexes it, and runs <c>fuse review</c> against the PR base, then scores the returned
///     context against the changed-file ground truth: changed-file recall, blast-radius precision, and
///     returned tokens at a budget.
/// </summary>
/// <remarks>
///     The ground truth is the changed C# files (Section 18.4 mode 1): review includes them as must-keep
///     seeds, so recall is expected to be high and the discriminating axes are precision (how tightly the
///     blast radius is scoped) and tokens. Corpus-bound: when no repository in the dataset is present on
///     disk, the suite returns a skip result rather than failing.
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
        var budget = options.Budgets is { Count: > 0 } ? options.Budgets[0] : DefaultBudget;
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1");
        var notes = new List<string> { $"token budget {budget:N0}" };

        var present = dataset.Repos
            .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped. Run setup-corpus to populate the corpus.");
            return Skipped(notes);
        }

        var tasks = new List<TaskResult>();
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var repo in present)
        {
            var repoTasks = options.Limit > 0 ? repo.Tasks.Take(options.Limit).ToList() : repo.Tasks;
            foreach (var task in repoTasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (result, mode) = await ScoreTaskAsync(manager, repo, task, budget, options, cancellationToken);
                if (result is not null)
                    tasks.Add(result);
                if (mode is not null)
                    modes[mode] = modes.GetValueOrDefault(mode) + 1;
            }
        }

        if (modes.Count > 0)
            notes.Add("index modes: " + string.Join(", ", modes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));

        if (tasks.Count == 0)
        {
            notes.Add("No tasks could be scored (indexing or worktree failures).");
            return Skipped(notes);
        }

        return Aggregate(tasks, notes);
    }

    private async Task<(TaskResult? Result, string? Mode)> ScoreTaskAsync(
        CorpusManager manager, RepoTasks repo, PrTask task, int budget, EvalOptions options, CancellationToken ct)
    {
        if (task.HeadRef is null || task.BaseRef is null)
            return (null, null);

        var worktree = await manager.AddWorktreeAsync(repo.Path!, task.HeadRef, ct);
        if (worktree is null)
        {
            options.Report($"review: {task.Id} worktree failed; skipped");
            return (null, null);
        }

        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-b", Guid.NewGuid().ToString("N"), "fuse.db");
        var stopwatch = Stopwatch.StartNew();
        string? mode = null;
        try
        {
            if (options.Restore)
                await manager.RestoreAsync(worktree, ct);

            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(ct);
            mode = (await _indexer.IndexAsync(worktree, store, ct)).Mode;

            // Fail loudly rather than silently scoring the syntax fallback when semantic mode is required.
            if (options.RequireSemantic && mode != "semantic")
            {
                options.Report($"review: {task.Id} indexed as {mode}, not semantic; skipped under --require-semantic");
                return (null, mode);
            }

            var engine = new SemanticRetrievalEngine(store, _changeSource);
            var plan = await engine.ReviewAsync(
                new ReviewRequest(worktree, task.BaseRef, MaxTokens: budget), ct);
            stopwatch.Stop();

            var retrieved = plan.Items
                .Select(i => Normalize(i.Path))
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groundTruth = task.GroundTruth.Files
                .Select(f => Normalize(f.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var recall = Metrics.Recall(retrieved, groundTruth);
            var precision = Metrics.Precision(retrieved.ToList(), groundTruth);
            options.Report($"review: {task.Id} recall {recall:P0} precision {precision:P0} tokens {plan.EstimatedTokens}");

            return (new TaskResult(
                task.Id, task.Repo, task.Category, recall, precision, plan.EstimatedTokens, stopwatch.ElapsedMilliseconds,
                new TaskFiles(
                    groundTruth.Where(retrieved.Contains).ToList(),
                    groundTruth.Where(g => !retrieved.Contains(g)).ToList(),
                    retrieved.Where(r => !groundTruth.Contains(r)).Take(20).ToList())), mode);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            options.Report($"review: {task.Id} failed: {e.Message}");
            return (null, mode);
        }
        finally
        {
            TryDeleteStore(databasePath);
            await manager.RemoveWorktreeAsync(repo.Path!, worktree, ct);
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
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={databasePath}"));
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
