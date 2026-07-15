using System.Text.Json;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite A: semantic resolution. Scores the semantic graph Fuse extracts against hand-built edge
///     ground truth (the moat: deterministic .NET wiring). Offline and deterministic; it indexes each
///     fixture into a throwaway store, reads the extracted edges, and compares them to the fixture's
///     <c>expected-edges.json</c>.
/// </summary>
/// <remarks>
///     Recall is matched edges over expected edges. Precision is measured only over the edge types that
///     appear in the ground truth, so correct edges of other types (for example structural
///     <c>implements</c> edges) are not counted as false positives (Section 18.10 edge gold files).
/// </remarks>
public sealed class SemanticResolutionSuite : IEvalSuite
{
    private readonly SemanticIndexer _indexer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticResolutionSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer used to build each fixture's graph.</param>
    public SemanticResolutionSuite(SemanticIndexer indexer) => _indexer = indexer;

    /// <inheritdoc />
    public string Name => "semantics";

    /// <inheritdoc />
    public string Description => "Semantic resolution: extracted graph edges vs hand-built edge ground truth.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        if (options.CorpusSample > 0)
            return await RunCorpusSampleAsync(options, cancellationToken);

        var fixturesRoot = options.FixturesRoot is { } root
            ? Path.GetFullPath(root)
            : DefaultFixturesRoot(options.BenchRoot);
        var fixtures = ResolveFixtures(fixturesRoot);
        if (fixtures.Count == 0)
        {
            return new SuiteResult(Name, Description, null,
                new Scorecard(0, 0, 0, 0, 0, 0, 0, 0),
                [],
                [$"No fixtures with expected-edges.json found under {fixturesRoot}; skipped."]);
        }

        var tasks = new List<TaskResult>();
        var recalls = new List<double>();
        var precisions = new List<double>();
        var totalExpected = 0;
        var totalMatched = 0;
        var totalFalsePositives = 0;

        foreach (var fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Report($"semantics: scoring {Path.GetFileName(fixture)}");
            var score = await ScoreFixtureAsync(fixture, cancellationToken);

            totalExpected += score.Expected;
            totalMatched += score.Matched;
            totalFalsePositives += score.FalsePositives;
            var recall = score.Expected == 0 ? 1.0 : (double)score.Matched / score.Expected;
            var precision = score.Matched + score.FalsePositives == 0
                ? 1.0
                : (double)score.Matched / (score.Matched + score.FalsePositives);
            recalls.Add(recall);
            precisions.Add(precision);

            tasks.Add(new TaskResult(
                Path.GetFileName(fixture), Path.GetFileName(fixture), "fixture",
                recall, precision, 0, 0,
                new TaskFiles(score.Matched > 0 ? [$"{score.Matched} edges"] : [],
                    score.Missed.Select(m => $"[{m.Type}] {m.From} -> {m.To}").ToList(),
                    score.FalsePositives > 0 ? [$"{score.FalsePositives} edges"] : [])));
        }

        var overallRecall = totalExpected == 0 ? 1.0 : (double)totalMatched / totalExpected;
        var overallPrecision = totalMatched + totalFalsePositives == 0
            ? 1.0
            : (double)totalMatched / (totalMatched + totalFalsePositives);
        var (ciLow, ciHigh) = Metrics.BootstrapCi(recalls);

        return new SuiteResult(Name, Description, null,
            new Scorecard(
                fixtures.Count,
                overallRecall, ciLow, ciHigh,
                overallPrecision,
                Metrics.F1(overallPrecision, overallRecall),
                0, 0, 0),
            tasks,
            [$"edges expected {totalExpected}, matched {totalMatched}, false positives {totalFalsePositives}"]);
    }

    // Corpus-adjudication mode: index each present repository, collect the predicted graph edges, sample a
    // fixed number per edge type with a seeded shuffle, and write the sample to results/semantics-corpus-sample.json
    // for a human or strong model to label. Precision with confidence intervals is reported once labels exist;
    // this run produces the reproducible sample and the per-type and per-mode counts.
    private async Task<SuiteResult> RunCorpusSampleAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);

        // Sample over the manifest repos when --manifest is set (corpus v2, B4), so the adjudication sample is drawn
        // from the buildable semantic-mode corpus, not the retired prs.json dataset. Otherwise fall back to the
        // dataset for backward compatibility.
        List<(string Name, string Path)> present;
        if (options.ManifestPath is not null)
        {
            present = manager.LoadManifest(options.ManifestPath).Repos
                .Where(r => options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase))
                .Select(r => (r.Name, Path: manager.ResolveRepoPath(r)))
                .Where(r => r.Path is not null && Directory.Exists(r.Path))
                .Select(r => (r.Name, r.Path!))
                .ToList();
        }
        else
        {
            present = manager.LoadDataset("dotnet-prs-v1").Repos
                .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
                .Select(r => (r.Name, r.Path!))
                .ToList();
        }

        var notes = new List<string> { $"sample {options.CorpusSample} edges per type" };
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped.");
            return new SuiteResult(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);
        }

        var predicted = new List<SampledEdge>();
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var repo in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Restore)
                await manager.RestoreAsync(repo.Path!, cancellationToken);

            var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-sample", Guid.NewGuid().ToString("N"), "fuse.db");
            try
            {
                await using var store = new WorkspaceIndexStore(databasePath);
                await store.InitializeAsync(cancellationToken);
                var mode = (await _indexer.IndexAsync(repo.Path!, store, cancellationToken)).Mode;
                modes[mode] = modes.GetValueOrDefault(mode) + 1;
                foreach (var edge in await store.GetAllEdgesAsync(cancellationToken))
                    predicted.Add(new SampledEdge(edge.FromNodeId, edge.ToNodeId, edge.EdgeType, repo.Name));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                options.Report($"semantics-sample: {repo.Name} failed: {e.Message}");
            }
            finally
            {
                TryDeleteStore(databasePath);
            }
        }

        notes.Add("index modes: " + string.Join(", ", modes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        notes.Add($"predicted edges: {predicted.Count}");
        var sample = EdgeSampler.Sample(predicted, options.CorpusSample, seed: 1469);
        foreach (var group in sample.GroupBy(e => e.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            notes.Add($"sampled {group.Key}: {group.Count()}");

        var samplePath = Path.Combine(options.ResultsRoot, "semantics-corpus-sample.json");
        Directory.CreateDirectory(options.ResultsRoot);
        await File.WriteAllTextAsync(samplePath,
            JsonSerializer.Serialize(sample.ToArray(), BenchmarkJsonContext.Default.SampledEdgeArray), cancellationToken);
        notes.Add($"wrote {sample.Count} sampled edges to {samplePath} for adjudication");

        return new SuiteResult(Name, Description, null,
            new Scorecard(sample.Count, 0, 0, 0, 0, 0, 0, 0), [], notes);
    }

    private async Task<FixtureScore> ScoreFixtureAsync(string fixtureDir, CancellationToken cancellationToken)
    {
        var expected = ReadExpectedEdges(Path.Combine(fixtureDir, "expected-edges.json"));

        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval", Guid.NewGuid().ToString("N"), "fuse.db");
        Edge[] predicted;
        try
        {
            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(cancellationToken);
            await _indexer.IndexAsync(fixtureDir, store, cancellationToken);
            predicted = (await store.GetAllEdgesAsync(cancellationToken))
                .Select(e => new Edge(e.FromNodeId, e.ToNodeId, e.EdgeType))
                .ToHashSet()
                .ToArray();
        }
        finally
        {
            TryDeleteStore(databasePath);
        }

        var predictedSet = predicted.ToHashSet();
        var matched = expected.Where(predictedSet.Contains).ToList();
        var missed = expected.Where(e => !predictedSet.Contains(e)).ToList();
        var scoredTypes = expected.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);
        var predictedInScope = predicted.Where(e => scoredTypes.Contains(e.Type)).ToList();
        var falsePositives = predictedInScope.Where(e => !expected.Contains(e)).ToList();

        return new FixtureScore(expected.Count, matched.Count, falsePositives.Count, missed);
    }

    private static string DefaultFixturesRoot(string benchRoot)
    {
        // Fixtures live at tests/fixtures relative to the repo root (benchRoot is tests/benchmarks).
        var repoRoot = Path.GetFullPath(Path.Combine(benchRoot, "..", ".."));
        return Path.Combine(repoRoot, "tests", "fixtures");
    }

    private static IReadOnlyList<string> ResolveFixtures(string root)
    {
        if (File.Exists(Path.Combine(root, "expected-edges.json")))
            return [root];
        if (!Directory.Exists(root))
            return [];
        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "expected-edges.json")))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<Edge> ReadExpectedEdges(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var edges = new HashSet<Edge>();
        foreach (var element in document.RootElement.GetProperty("edges").EnumerateArray())
        {
            edges.Add(new Edge(
                element.GetProperty("from").GetString()!,
                element.GetProperty("to").GetString()!,
                element.GetProperty("type").GetString()!));
        }

        return edges;
    }

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
            // Best-effort cleanup.
        }
    }

    private readonly record struct Edge(string From, string To, string Type);

    private sealed record FixtureScore(int Expected, int Matched, int FalsePositives, IReadOnlyList<Edge> Missed);
}
