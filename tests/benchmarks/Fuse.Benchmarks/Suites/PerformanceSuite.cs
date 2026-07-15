using System.Diagnostics;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     Performance: cold index time and warm operation latency. Indexes a present corpus repository once
///     (the cold index), then runs resolve and localize repeatedly over the warm index and reports P50 and
///     P95 latency per operation. Warm-and-fast-from-a-persistent-index is part of the V3 claim and was
///     previously unmeasured.
/// </summary>
/// <remarks>
///     Corpus-bound: when no repository in the dataset is present on disk, the suite returns a skip result.
///     Timings are environment-dependent (machine, disk, index mode), so read them as an order-of-magnitude
///     signal, not a fixed cross-machine number. The suite measures cold index, warm operation latency, and
///     single-file incremental re-index (which updates the file's syntax rows, not cross-file semantic edges).
/// </remarks>
public sealed class PerformanceSuite : IEvalSuite
{
    private const int WarmIterations = 25;
    private readonly SemanticIndexer _indexer;
    private readonly IChangeSource _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PerformanceSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="changeSource">The git change source.</param>
    public PerformanceSuite(SemanticIndexer indexer, IChangeSource changeSource)
    {
        _indexer = indexer;
        _changeSource = changeSource;
    }

    /// <inheritdoc />
    public string Name => "performance";

    /// <inheritdoc />
    public string Description => "Performance: cold index time and warm resolve/localize P50/P95 latency.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1");
        var notes = new List<string>();

        var repo = dataset.Repos.FirstOrDefault(r =>
            r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)));
        if (repo?.Path is null)
        {
            notes.Add("No corpus repository present; skipped. Run setup-corpus to populate the corpus.");
            return Skipped(notes);
        }

        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-perf", Guid.NewGuid().ToString("N"), "fuse.db");
        try
        {
            if (options.Restore)
                await manager.RestoreAsync(repo.Path, cancellationToken);

            // Cold-start (A4): time the syntax-first pass that serves a first call, separately from the full
            // semantic load, on a throwaway store, so "time to first syntax-tier answer" and "time to
            // semantic-ready" are reported as distinct numbers.
            var coldStartPath = Path.Combine(Path.GetTempPath(), "fuse-eval-coldstart", Guid.NewGuid().ToString("N"), "fuse.db");
            try
            {
                await using var coldStore = new WorkspaceIndexStore(coldStartPath);
                await coldStore.InitializeAsync(cancellationToken);
                var syntaxWatch = Stopwatch.StartNew();
                var syntaxFirst = await _indexer.IndexSyntaxFirstAsync(repo.Path, coldStore, cancellationToken);
                syntaxWatch.Stop();
                var pending = await coldStore.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
                var upgradeWatch = Stopwatch.StartNew();
                var upgraded = await _indexer.UpgradeToSemanticAsync(repo.Path, coldStore, cancellationToken);
                upgradeWatch.Stop();
                var cleared = await coldStore.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
                notes.Add($"cold start: syntax-tier served in {syntaxWatch.ElapsedMilliseconds:N0} ms (mode {syntaxFirst.Mode}, semantic_pending={pending}); semantic-ready after a further {upgradeWatch.ElapsedMilliseconds:N0} ms (mode {upgraded.Mode}, semantic_pending={cleared})");
            }
            finally
            {
                TryDeleteStore(coldStartPath);
            }

            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(cancellationToken);

            var coldWatch = Stopwatch.StartNew();
            var index = await _indexer.IndexAsync(repo.Path, store, cancellationToken);
            coldWatch.Stop();
            notes.Add($"repo {repo.Name}, index mode {index.Mode}, {index.FileCount} files, {index.SymbolCount} symbols");
            notes.Add($"cold index (full semantic pass): {coldWatch.ElapsedMilliseconds:N0} ms");

            // Warm operations over the persistent index. Use the most common indexed symbol as a realistic,
            // repo-agnostic resolve and localize target.
            var topSymbol = (await store.ListSymbolsAsync(1, cancellationToken)).FirstOrDefault()?.Name ?? repo.Name;
            var engine = new SemanticRetrievalEngine(store, _changeSource);
            var resolver = new SemanticResolver(store);

            var localizeMs = await TimeAsync(WarmIterations, async () =>
                await engine.LocalizeAsync(new LocalizationRequest(repo.Path, Query: topSymbol, MaxCandidates: 20), cancellationToken), cancellationToken);
            var resolveMs = await TimeAsync(WarmIterations, async () =>
                await resolver.ResolveSymbolAsync(topSymbol, cancellationToken), cancellationToken);

            notes.Add($"warm localize ({WarmIterations}x): P50 {Percentile(localizeMs, 50):F1} ms, P95 {Percentile(localizeMs, 95):F1} ms");
            notes.Add($"warm resolve ({WarmIterations}x): P50 {Percentile(resolveMs, 50):F1} ms, P95 {Percentile(resolveMs, 95):F1} ms");

            // The exact-lookup and blast-radius verbs behind fuse_find and fuse_impact (B2): warm reads over the
            // persistent index, the same primitives the MCP tools call.
            var findMs = await TimeAsync(WarmIterations, async () =>
                await store.FindSymbolsByNameAsync(topSymbol, 50, cancellationToken), cancellationToken);
            var explorer = new GraphNeighborhoodExplorer(store);
            var impactMs = await TimeAsync(WarmIterations, async () =>
                await explorer.CallersAndImplementersAsync(topSymbol, 50, cancellationToken), cancellationToken);
            notes.Add($"warm find symbol ({WarmIterations}x): P50 {Percentile(findMs, 50):F1} ms, P95 {Percentile(findMs, 95):F1} ms");
            notes.Add($"warm impact callers+implementers ({WarmIterations}x): P50 {Percentile(impactMs, 50):F1} ms, P95 {Percentile(impactMs, 95):F1} ms");

            // Review-plan latency against the checkout's own recent history (a real git base).
            try
            {
                var reviewMs = await TimeAsync(WarmIterations, async () =>
                    await engine.ReviewAsync(new ReviewRequest(repo.Path, "HEAD~1", MaxTokens: 25_000), cancellationToken), cancellationToken);
                notes.Add($"warm review plan ({WarmIterations}x): P50 {Percentile(reviewMs, 50):F1} ms, P95 {Percentile(reviewMs, 95):F1} ms");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                notes.Add($"warm review plan: skipped ({e.Message})");
            }

            // Single-file incremental re-index: clear and re-extract one file's rows, timed over repeats.
            var sampleFile = (await store.FindFilesByPathAsync(".cs", 1, cancellationToken)).FirstOrDefault()?.NormalizedPath;
            if (sampleFile is not null)
            {
                var incrementalMs = await TimeAsync(WarmIterations, async () =>
                    await _indexer.ReindexFileAsync(repo.Path, sampleFile, store, cancellationToken), cancellationToken);
                notes.Add($"incremental re-index of {sampleFile} ({WarmIterations}x): P50 {Percentile(incrementalMs, 50):F1} ms, P95 {Percentile(incrementalMs, 95):F1} ms");
            }

            notes.Add("note: timings are environment-dependent; incremental re-index updates the file's syntax rows, not cross-file semantic edges.");

            // The scorecard's token columns are reused to surface the headline latencies for the JSON readers:
            // medianTokens carries warm localize P50, meanTokens carries cold index ms.
            return new SuiteResult(Name, Description, null,
                new Scorecard(1, 0, 0, 0, 0, 0, Percentile(localizeMs, 50), coldWatch.ElapsedMilliseconds, 0),
                [], notes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add($"performance run failed: {e.Message}");
            return Skipped(notes);
        }
        finally
        {
            TryDeleteStore(databasePath);
        }
    }

    private static async Task<List<double>> TimeAsync(int iterations, Func<Task> operation, CancellationToken cancellationToken)
    {
        // One untimed warm-up so first-call JIT and cache priming do not skew the percentiles.
        await operation();
        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            await operation();
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        return samples;
    }

    /// <summary>
    ///     Returns the linear-interpolation percentile of a sample.
    /// </summary>
    /// <param name="values">The sample values.</param>
    /// <param name="percentile">The percentile in <c>[0, 100]</c>.</param>
    /// <returns>The percentile value, or <c>0</c> for an empty sample.</returns>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1)
            return sorted[0];
        var rank = percentile / 100.0 * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
            return sorted[low];
        return sorted[low] + (rank - low) * (sorted[high] - sorted[low]);
    }

    private SuiteResult Skipped(IReadOnlyList<string> notes)
        => new(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);

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
}
