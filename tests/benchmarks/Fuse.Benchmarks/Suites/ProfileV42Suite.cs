using System.Diagnostics;
using System.Text.Json;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     R8 index hot-path profile: warm localize, symbol lookup, review planning, and single-file reconcile
///     with lightweight SQL and Roslyn stack attribution for store-split ordering.
/// </summary>
public sealed class ProfileV42Suite : IEvalSuite
{
    private const int TopHotspotFrames = 10;
    private readonly SemanticIndexer _indexer;
    private readonly IChangeSource _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ProfileV42Suite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="changeSource">The git change source.</param>
    public ProfileV42Suite(SemanticIndexer indexer, IChangeSource changeSource)
    {
        _indexer = indexer;
        _changeSource = changeSource;
    }

    /// <inheritdoc />
    public string Name => "profile-v42";

    /// <inheritdoc />
    public string Description =>
        "R8 index hot-path profile: warm localize, symbol lookup, review planning, and single-file reconcile.";

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

        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-profile-v42", Guid.NewGuid().ToString("N"), "fuse.db");
        var sampler = new StackSampler();
        try
        {
            if (options.Restore)
                await manager.RestoreAsync(repo.Path, cancellationToken);

            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(cancellationToken);

            var coldWatch = Stopwatch.StartNew();
            var index = await _indexer.IndexAsync(repo.Path, store, cancellationToken);
            coldWatch.Stop();
            notes.Add($"repo {repo.Name}, index mode {index.Mode}, {index.FileCount} files, {index.SymbolCount} symbols");
            notes.Add($"cold index: {coldWatch.ElapsedMilliseconds:N0} ms");

            var topSymbol = (await store.ListSymbolsAsync(1, cancellationToken)).FirstOrDefault()?.Name ?? repo.Name;
            var engine = new SemanticRetrievalEngine(store, _changeSource);
            var sampleFile = (await store.FindFilesByPathAsync(".cs", 1, cancellationToken)).FirstOrDefault()?.NormalizedPath;

            var localize = await ProfileOperationAsync(
                ProfileV42Report.DefaultIterations,
                async () => await engine.LocalizeAsync(
                    new LocalizationRequest(repo.Path, Query: topSymbol, MaxCandidates: 20),
                    cancellationToken),
                sampler,
                cancellationToken);

            var findSymbol = await ProfileOperationAsync(
                ProfileV42Report.DefaultIterations,
                async () => await store.FindSymbolsByNameAsync(topSymbol, 50, cancellationToken),
                sampler,
                cancellationToken);

            ProfileV42OperationMetrics reviewPlan;
            try
            {
                reviewPlan = await ProfileOperationAsync(
                    ProfileV42Report.DefaultIterations,
                    async () => await engine.ReviewAsync(
                        new ReviewRequest(repo.Path, "HEAD~1", MaxTokens: 25_000),
                        cancellationToken),
                    sampler,
                    cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                notes.Add($"review plan: skipped ({e.Message})");
                reviewPlan = EmptyMetrics();
            }

            ProfileV42OperationMetrics reconcile;
            if (sampleFile is null)
            {
                notes.Add("reconcile: skipped (no sample file)");
                reconcile = EmptyMetrics();
            }
            else
            {
                reconcile = await ProfileOperationAsync(
                    ProfileV42Report.DefaultIterations,
                    async () => await _indexer.ReindexFileAsync(repo.Path, sampleFile, store, cancellationToken),
                    sampler,
                    cancellationToken);
                notes.Add($"reconcile target: {sampleFile}");
            }

            notes.Add($"localize P50 {localize.P50Ms:F1} ms P95 {localize.P95Ms:F1} ms");
            notes.Add($"findSymbol P50 {findSymbol.P50Ms:F1} ms P95 {findSymbol.P95Ms:F1} ms");
            if (reviewPlan.SampleCount > 0)
                notes.Add($"reviewPlan P50 {reviewPlan.P50Ms:F1} ms P95 {reviewPlan.P95Ms:F1} ms");
            if (reconcile.SampleCount > 0)
                notes.Add($"reconcile P50 {reconcile.P50Ms:F1} ms P95 {reconcile.P95Ms:F1} ms");

            var hotspots = sampler.BuildHotspots(TopHotspotFrames);
            var report = new ProfileV42Report(
                SchemaVersion: ProfileV42Report.CurrentSchemaVersion,
                FuseVersion: FuseBuildInfo.Current,
                Suite: Name,
                Description: Description,
                Generated: DateTime.UtcNow.ToString("O"),
                Placeholder: false,
                Repo: repo.Name,
                IndexMode: index.Mode,
                FileCount: index.FileCount,
                SymbolCount: index.SymbolCount,
                Iterations: ProfileV42Report.DefaultIterations,
                Operations: new ProfileV42Operations(localize, findSymbol, reviewPlan, reconcile),
                Hotspots: hotspots,
                Notes:
                [
                    "methodology: warm a persistent index, run each verb 25 times, attribute stack samples to SQLite and Roslyn frames for R9 store-split ordering.",
                    "compare wall-clock P50/P95 to performance.json for the same verbs; this artifact adds self-time and top-frame attribution, not a new SLO headline.",
                    ..notes
                ]);

            Directory.CreateDirectory(options.ResultsRoot);
            var reportPath = Path.Combine(options.ResultsRoot, ProfileV42Report.FileName);
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.ProfileV42Report),
                cancellationToken);
            options.Report($"profile-v42: wrote {reportPath}");

            return new SuiteResult(
                Name,
                Description,
                report.Generated,
                new Scorecard(1, 0, 0, 0, 0, 0, localize.P50Ms, localize.P95Ms),
                [],
                notes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add($"profile-v42 run failed: {e.Message}");
            return Skipped(notes);
        }
        finally
        {
            TryDeleteStore(databasePath);
        }
    }

    private static ProfileV42OperationMetrics EmptyMetrics() => new(0, 0, 0, 0);

    private static async Task<ProfileV42OperationMetrics> ProfileOperationAsync(
        int iterations,
        Func<Task> operation,
        StackSampler sampler,
        CancellationToken cancellationToken)
    {
        await operation();
        var timings = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sampler.BeginOperation();
            var watch = Stopwatch.StartNew();
            await operation();
            watch.Stop();
            sampler.EndOperation();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        return new ProfileV42OperationMetrics(
            PerformanceSuite.Percentile(timings, 50),
            PerformanceSuite.Percentile(timings, 95),
            sampler.LastOperationSelfTimePercent,
            timings.Count);
    }

    private SuiteResult Skipped(IReadOnlyList<string> notes)
        => new(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);

    private static void TryDeleteStore(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StackSampler
    {
        private readonly Dictionary<string, int> _sql = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _roslyn = new(StringComparer.Ordinal);
        private int _operationTotalSamples;
        private int _operationTotalAttributed;

        public double LastOperationSelfTimePercent { get; private set; }

        public void BeginOperation()
        {
            _operationTotalSamples = 0;
            _operationTotalAttributed = 0;
            LastOperationSelfTimePercent = 0;
        }

        public void EndOperation() => SampleCurrentStack();

        private void SampleCurrentStack()
        {
            var trace = new StackTrace(2, false);
            foreach (var frame in trace.GetFrames() ?? [])
            {
                var method = frame.GetMethod();
                var declaringType = method?.DeclaringType;
                if (declaringType is null)
                    continue;

                _operationTotalSamples++;
                var key = $"{declaringType.FullName}.{method!.Name}";
                if (IsSqlFrame(declaringType.FullName))
                {
                    _operationTotalAttributed++;
                    _sql[key] = _sql.GetValueOrDefault(key) + 1;
                }
                else if (IsRoslynFrame(declaringType.FullName))
                {
                    _operationTotalAttributed++;
                    _roslyn[key] = _roslyn.GetValueOrDefault(key) + 1;
                }
            }

            LastOperationSelfTimePercent = _operationTotalSamples == 0
                ? 0
                : 100.0 * _operationTotalAttributed / _operationTotalSamples;
        }

        public ProfileV42Hotspots BuildHotspots(int topN)
            => new(ToHotspotFrames(_sql, topN), ToHotspotFrames(_roslyn, topN));

        private static IReadOnlyList<ProfileV42HotspotFrame> ToHotspotFrames(
            Dictionary<string, int> buckets,
            int topN)
        {
            var total = buckets.Values.Sum();
            if (total == 0)
                return [];

            return buckets
                .OrderByDescending(pair => pair.Value)
                .Take(topN)
                .Select(pair => new ProfileV42HotspotFrame(
                    pair.Key,
                    100.0 * pair.Value / total,
                    pair.Value))
                .ToList();
        }

        private static bool IsSqlFrame(string? typeName) =>
            typeName is not null && (
                typeName.Contains("Sqlite", StringComparison.Ordinal) ||
                typeName.StartsWith("Fuse.Indexing", StringComparison.Ordinal));

        private static bool IsRoslynFrame(string? typeName) =>
            typeName is not null && (
                typeName.Contains("CodeAnalysis", StringComparison.Ordinal) ||
                typeName.StartsWith("Fuse.Semantics", StringComparison.Ordinal) ||
                typeName.Contains("CSharp", StringComparison.Ordinal));
    }
}
