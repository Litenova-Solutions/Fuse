using System.Text;
using System.Text.Json;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Runs Fuse's evaluation suites. The semantics suite (Suite A) scores the semantic graph Fuse extracts
///     against hand-built edge ground truth: per-edge-type recall and precision, plus the missed edges.
/// </summary>
/// <remarks>
///     The semantics suite is offline and deterministic: it indexes a fixture, reads the resulting edges, and
///     compares them to the fixture's <c>expected-edges.json</c>. Recall is matched edges over expected edges;
///     precision is measured only over the edge types present in the ground truth, so correct edges of other
///     types (for example structural <c>implements</c> edges) are not counted as false positives.
/// </remarks>
[CliCommand(
    Name = "eval",
    Description = "Run Fuse evaluation suites (semantics: score the semantic graph against edge ground truth).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class EvalCommand
{
    private readonly SemanticIndexer _indexer;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EvalCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public EvalCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="EvalCommand" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="consoleUI">The console UI for output.</param>
    public EvalCommand(SemanticIndexer indexer, IConsoleUI consoleUI)
    {
        _indexer = indexer;
        _consoleUI = consoleUI;
    }

    /// <summary>The suite to run. Currently <c>semantics</c>.</summary>
    [CliArgument(Description = "The suite to run: semantics.")]
    public string Suite { get; set; } = "semantics";

    /// <summary>
    ///     The fixtures root. Each immediate subdirectory containing an <c>expected-edges.json</c> is a fixture;
    ///     a fixtures path that itself contains <c>expected-edges.json</c> is treated as a single fixture.
    /// </summary>
    [CliOption(Description = "Fixtures root (a directory of fixtures, or a single fixture directory).")]
    public string Fixtures { get; set; } = ".";

    /// <summary>An optional path to write the JSON results to.</summary>
    [CliOption(Required = false, Description = "Path to write JSON results to.")]
    public string? Output { get; set; }

    /// <summary>
    ///     Runs the eval command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the suite has run.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!Suite.Trim().Equals("semantics", StringComparison.OrdinalIgnoreCase))
        {
            _consoleUI.WriteError($"Unknown suite '{Suite}'. Supported: semantics.");
            return;
        }

        var root = Path.GetFullPath(Fixtures);
        var fixtures = ResolveFixtures(root);
        if (fixtures.Count == 0)
        {
            _consoleUI.WriteError($"No fixtures with expected-edges.json found under {root}.");
            return;
        }

        var results = new List<FixtureScore>();
        foreach (var fixture in fixtures)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScoreFixtureAsync(fixture, context.CancellationToken));
        }

        var report = FormatReport(results);
        _consoleUI.WriteResult(report);

        if (!string.IsNullOrWhiteSpace(Output))
        {
            await File.WriteAllTextAsync(Path.GetFullPath(Output), ToJson(results), context.CancellationToken);
            _consoleUI.WriteStep($"Wrote results to {Path.GetFullPath(Output)}");
        }
    }

    private async Task<FixtureScore> ScoreFixtureAsync(string fixtureDir, CancellationToken cancellationToken)
    {
        var expected = ReadExpectedEdges(Path.Combine(fixtureDir, "expected-edges.json"));

        // Index into a throwaway store so the evaluation never pollutes the workspace's own index.
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
        // Precision is scored only over edge types that appear in the ground truth.
        var scoredTypes = expected.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);
        var predictedInScope = predicted.Where(e => scoredTypes.Contains(e.Type)).ToList();
        var falsePositives = predictedInScope.Where(e => !expected.Contains(e)).ToList();

        return new FixtureScore(Path.GetFileName(fixtureDir), expected.Count, matched.Count, falsePositives.Count, missed);
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

    private static string FormatReport(IReadOnlyList<FixtureScore> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Fuse Eval: semantic resolution (Suite A)");
        var totalExpected = results.Sum(r => r.Expected);
        var totalMatched = results.Sum(r => r.Matched);
        var totalFalsePositives = results.Sum(r => r.FalsePositives);
        builder.AppendLine($"fixtures: {results.Count}  edges expected: {totalExpected}");
        builder.AppendLine($"recall: {Ratio(totalMatched, totalExpected):P0} ({totalMatched}/{totalExpected})   " +
                           $"precision: {Ratio(totalMatched, totalMatched + totalFalsePositives):P0} " +
                           $"({totalMatched}/{totalMatched + totalFalsePositives})   F1: {F1(totalMatched, totalFalsePositives, totalExpected - totalMatched):F2}");
        builder.AppendLine();

        foreach (var result in results)
        {
            builder.AppendLine($"  {result.Name}: recall {result.Matched}/{result.Expected}, false positives {result.FalsePositives}");
            foreach (var miss in result.Missed)
                builder.AppendLine($"    MISS [{miss.Type}] {miss.From} -> {miss.To}");
        }

        return builder.ToString();
    }

    private static string ToJson(IReadOnlyList<FixtureScore> results)
    {
        var totalExpected = results.Sum(r => r.Expected);
        var totalMatched = results.Sum(r => r.Matched);
        var totalFalsePositives = results.Sum(r => r.FalsePositives);
        var dto = new EvalResultsDto(
            Suite: "semantics",
            Fixtures: results.Count,
            EdgesExpected: totalExpected,
            EdgesMatched: totalMatched,
            FalsePositives: totalFalsePositives,
            Recall: Ratio(totalMatched, totalExpected),
            Precision: Ratio(totalMatched, totalMatched + totalFalsePositives),
            Results: results.Select(r => new FixtureResultDto(
                r.Name, r.Expected, r.Matched, r.FalsePositives,
                r.Missed.Select(m => $"[{m.Type}] {m.From} -> {m.To}").ToList())).ToList());
        return JsonSerializer.Serialize(dto, FuseEvalJsonContext.Default.EvalResultsDto);
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 1.0 : (double)numerator / denominator;

    private static double F1(int truePositives, int falsePositives, int falseNegatives)
    {
        var precision = Ratio(truePositives, truePositives + falsePositives);
        var recall = Ratio(truePositives, truePositives + falseNegatives);
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static void TryDeleteStore(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"));
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private readonly record struct Edge(string From, string To, string Type);

    private sealed record FixtureScore(string Name, int Expected, int Matched, int FalsePositives, IReadOnlyList<Edge> Missed);
}
