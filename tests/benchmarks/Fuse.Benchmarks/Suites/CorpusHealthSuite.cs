using System.Text.Json;
using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Benchmarks;

/// <summary>
///     The corpus-health suite (C4): proves the benchmark corpus is a usable arena for the model-driven suites.
///     For each repository it restores and indexes the checkout to record the achieved index tier, and discovers
///     the test suite (test projects and test source files); it writes a machine-readable
///     <see cref="CorpusHealthReport" /> to <c>results/corpus-health.json</c>. The model-driven suites (loop,
///     agent) read that report and refuse to start unless it is newer than the corpus manifest and meets the
///     minimums, so a corpus that does not build cannot be mistaken for one that does.
/// </summary>
public sealed class CorpusHealthSuite : IEvalSuite
{
    private readonly SemanticIndexer _indexer;

    /// <summary>Initializes a new instance of the <see cref="CorpusHealthSuite" /> class.</summary>
    /// <param name="indexer">The semantic indexer used to record each repository's achieved tier.</param>
    public CorpusHealthSuite(SemanticIndexer indexer) => _indexer = indexer;

    /// <inheritdoc />
    public string Name => "corpus-health";

    /// <inheritdoc />
    public string Description => "Corpus health: per-repo achieved tier and test discovery, plus the model-suite gate minimums.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var manifest = manager.LoadManifest(options.ManifestPath);
        var notes = new List<string>();

        var repos = manifest.Repos
            .Where(r => options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var health = new List<CorpusRepoHealth>();
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = manager.ResolveRepoPath(repo);
            if (path is null || !Directory.Exists(path))
            {
                health.Add(new CorpusRepoHealth(repo.Name, "absent", Tier1: false, TestProjects: 0, TestFiles: 0, Note: "not present; run setup-corpus"));
                continue;
            }

            if (options.Restore)
                await manager.RestoreAsync(path, cancellationToken);

            string tier;
            var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-health", Guid.NewGuid().ToString("N"), "fuse.db");
            try
            {
                await using var store = new WorkspaceIndexStore(databasePath);
                await store.InitializeAsync(cancellationToken);
                tier = (await _indexer.IndexAsync(path, store, cancellationToken)).Mode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tier = "error";
                notes.Add($"{repo.Name}: index error: {ex.Message}");
            }
            finally
            {
                TryDeleteStore(databasePath);
            }

            var (testProjects, testFiles) = DiscoverTests(path);
            health.Add(new CorpusRepoHealth(
                repo.Name, tier, Tier1: tier == "semantic", testProjects, testFiles,
                Note: testProjects == 0 ? "no test project discovered" : null));
        }

        var report = new CorpusHealthReport(
            Generated: DateTime.UtcNow.ToString("O"),
            ReposTotal: repos.Count,
            ReposTier1: health.Count(h => h.Tier1),
            TasksTotal: 0,
            TasksVerified: 0,
            MinReposTier1: CorpusHealthReport.GateMinReposTier1,
            MinTasksVerified: CorpusHealthReport.GateMinTasksVerified,
            Repos: health,
            Notes: notes);

        Directory.CreateDirectory(options.ResultsRoot);
        var reportPath = Path.Combine(options.ResultsRoot, CorpusHealthReport.FileName);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.CorpusHealthReport),
            cancellationToken);
        options.Report($"corpus-health: wrote {reportPath} (tier-1 {report.ReposTier1}/{report.ReposTotal}, verified tasks {report.TasksVerified}, meetsMinimums {report.MeetsMinimums})");

        // Oracle-task verification (>= 60 tasks) is the 4b sub-step; this report records 0 verified tasks until
        // then, so MeetsMinimums stays false here even when tier-1 repos are counted. Recorded, not hidden.
        notes.Add($"tier-1 repos {report.ReposTier1}/{report.ReposTotal}; verified oracle tasks {report.TasksVerified} (task-oracle extraction is C4 sub-step 4b); meetsMinimums {report.MeetsMinimums} (needs >= {report.MinReposTier1} tier-1 repos and >= {report.MinTasksVerified} verified tasks).");

        var summaryNotes = new List<string> { $"corpus-health report at {CorpusHealthReport.FileName}" };
        summaryNotes.AddRange(notes);
        summaryNotes.AddRange(health.Select(h => $"{h.Name}: tier {h.Tier}, test projects {h.TestProjects}, test files {h.TestFiles}{(h.Note is null ? "" : $" ({h.Note})")}"));

        return new SuiteResult(
            Name,
            Description,
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            new Scorecard(TaskCount: report.TasksVerified, 0, 0, 0, 0, 0, 0, 0),
            [],
            summaryNotes);
    }

    // Counts test projects (csproj on a test path) and test source files (*.cs on a test path) by scanning the
    // repository. A cheap structural signal that a repository has a runnable suite, ahead of the 4b oracle run.
    private static (int TestProjects, int TestFiles) DiscoverTests(string repoPath)
    {
        var testProjects = 0;
        var testFiles = 0;
        foreach (var file in Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories))
        {
            var lower = file.Replace('\\', '/').ToLowerInvariant();
            if (lower.Contains("/bin/") || lower.Contains("/obj/") || lower.Contains("/.git/"))
                continue;
            if (lower.EndsWith(".csproj") && IsTestPath(lower))
                testProjects++;
            else if (lower.EndsWith(".cs") && IsTestPath(lower))
                testFiles++;
        }

        return (testProjects, testFiles);
    }

    // A path is a test path when a segment or file name signals a test project or test file.
    private static bool IsTestPath(string lowerPath) =>
        lowerPath.Contains("/test") || lowerPath.Contains(".test") || lowerPath.Contains("tests/");

    // Best-effort cleanup of the temporary per-repo store, so a health run leaves no scratch behind.
    private static void TryDeleteStore(string databasePath)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"));
            var directory = Path.GetDirectoryName(databasePath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            // A leftover scratch store is harmless.
        }
    }
}
