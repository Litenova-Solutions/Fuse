using System.Diagnostics;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite C: open-ended localization (the old weak spot). With no git base, can <c>fuse localize</c>
///     locate the files a task touches from its title alone, and just as importantly, can it detect when
///     a title carries no usable signal rather than returning overconfident junk?
/// </summary>
/// <remarks>
///     Each repository is indexed once at its pinned commit; every PR's title is then localized against
///     that index with the diff hidden. Recall is bucketed by signal (Section 18.5): identifier-rich and
///     route/API titles should localize well, while no-signal titles (merge noise) should be detected as
///     low signal. Low-signal detection F1 scores the binary classifier: a true positive is a no-signal
///     title that Fuse flags (empty candidate set or a warning); a false positive is downgrading a title
///     it could have solved.
/// </remarks>
public sealed class LocalizationSuite : IEvalSuite
{
    private const int CandidateK = 20;
    private readonly SemanticIndexer _indexer;
    private readonly IChangeSource _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LocalizationSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="changeSource">The git change source (unused for title-only localization, passed for parity).</param>
    public LocalizationSuite(SemanticIndexer indexer, IChangeSource changeSource)
    {
        _indexer = indexer;
        _changeSource = changeSource;
    }

    /// <inheritdoc />
    public string Name => "localize";

    /// <inheritdoc />
    public string Description => "Open-ended localization: recall by signal bucket and low-signal detection.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1");
        var notes = new List<string> { $"candidates@{CandidateK}, title-only input" };

        var present = dataset.Repos
            .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped. Run setup-corpus to populate the corpus.");
            return Skipped(notes);
        }

        var tasks = new List<TaskResult>();
        var detection = new List<(bool LowSignal, bool Detected)>();
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var repo in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Restore)
            {
                var restore = await manager.RestoreAsync(repo.Path!, cancellationToken);
                notes.Add($"restore {repo.Name}: {restore.Summary}");
            }

            options.Report($"localize: indexing {repo.Name}");
            var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-c", Guid.NewGuid().ToString("N"), "fuse.db");
            try
            {
                await using var store = new WorkspaceIndexStore(databasePath);
                await store.InitializeAsync(cancellationToken);
                var mode = (await _indexer.IndexAsync(repo.Path!, store, cancellationToken)).Mode;
                modes[mode] = modes.GetValueOrDefault(mode) + 1;

                // Fail loudly rather than silently scoring the syntax fallback when semantic mode is required.
                if (options.RequireSemantic && mode != "semantic")
                {
                    notes.Add($"require-semantic: {repo.Name} indexed as {mode}, not semantic; tasks skipped (not scored at fallback).");
                    options.Report($"localize: {repo.Name} below semantic ({mode}); skipped under --require-semantic");
                    continue;
                }

                var engine = new SemanticRetrievalEngine(store, _changeSource, options.Embedder);
                var repoTasks = options.Limit > 0 ? repo.Tasks.Take(options.Limit).ToList() : repo.Tasks;
                foreach (var task in repoTasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (result, detected) = await ScoreTaskAsync(engine, repo.Path!, task, options, cancellationToken);
                    tasks.Add(result);
                    detection.Add((SignalBucket.IsLowSignal(task.Category), detected));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                options.Report($"localize: {repo.Name} indexing failed: {e.Message}");
            }
            finally
            {
                TryDeleteStore(databasePath);
            }
        }

        if (tasks.Count == 0)
        {
            notes.Add("No tasks could be scored.");
            return Skipped(notes);
        }

        notes.Add("index modes: " + string.Join(", ", modes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        AddBucketNotes(tasks, notes);
        return Aggregate(tasks, detection, notes);
    }

    private async Task<(TaskResult Result, bool Detected)> ScoreTaskAsync(
        SemanticRetrievalEngine engine, string root, PrTask task, EvalOptions options, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var localization = await engine.LocalizeAsync(
            new LocalizationRequest(root, Query: task.Title, MaxCandidates: CandidateK), ct);
        stopwatch.Stop();

        var retrieved = localization.Candidates
            .Select(c => Normalize(c.Path))
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groundTruth = task.GroundTruth.Files
            .Select(f => Normalize(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recall = Metrics.Recall(retrieved, groundTruth);
        var precision = Metrics.Precision(retrieved.ToList(), groundTruth);
        var tokens = localization.Candidates.Sum(c => c.EstimatedTokens);
        // R3: detection is the engine's explicit low-signal verdict (it abstained and suggested a next input),
        // not the incidental "no candidates" heuristic, so the score measures the classifier, not luck.
        var detected = localization.LowSignal;

        var result = new TaskResult(
            task.Id, task.Repo, task.Category, recall, precision, tokens, stopwatch.ElapsedMilliseconds,
            new TaskFiles(
                groundTruth.Where(retrieved.Contains).ToList(),
                groundTruth.Where(g => !retrieved.Contains(g)).ToList(),
                retrieved.Where(r => !groundTruth.Contains(r)).Take(20).ToList()));
        return (result, detected);
    }

    private SuiteResult Aggregate(
        IReadOnlyList<TaskResult> tasks, IReadOnlyList<(bool LowSignal, bool Detected)> detection, List<string> notes)
    {
        var recalls = tasks.Select(t => t.Recall).ToList();
        var precisions = tasks.Select(t => t.Precision).ToList();
        var tokens = tasks.Select(t => (double)t.Tokens).ToList();
        var (ciLow, ciHigh) = Metrics.BootstrapCi(recalls);
        var meanRecall = Metrics.Mean(recalls);
        var meanPrecision = Metrics.Mean(precisions);

        // Low-signal detection: positive class = no-signal titles; "detected" = empty result or warning.
        var truePositive = detection.Count(d => d.LowSignal && d.Detected);
        var falsePositive = detection.Count(d => !d.LowSignal && d.Detected);
        var falseNegative = detection.Count(d => d.LowSignal && !d.Detected);
        var detPrecision = truePositive + falsePositive == 0 ? 0 : (double)truePositive / (truePositive + falsePositive);
        var detRecall = truePositive + falseNegative == 0 ? 1.0 : (double)truePositive / (truePositive + falseNegative);
        var lowSignalF1 = Metrics.F1(detPrecision, detRecall);
        notes.Add($"low-signal detection: tp {truePositive}, fp {falsePositive}, fn {falseNegative} (precision {detPrecision:P0}, recall {detRecall:P0})");

        return new SuiteResult(Name, Description, null,
            new Scorecard(
                tasks.Count, meanRecall, ciLow, ciHigh, meanPrecision,
                Metrics.F1(meanPrecision, meanRecall),
                Metrics.Median(tokens), Metrics.Mean(tokens), lowSignalF1),
            tasks, notes);
    }

    private static void AddBucketNotes(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        foreach (var group in tasks.GroupBy(t => t.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var recall = Metrics.Mean(group.Select(t => t.Recall).ToList());
            notes.Add($"bucket {group.Key}: n {group.Count()}, mean recall {recall:P0}");
        }
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
