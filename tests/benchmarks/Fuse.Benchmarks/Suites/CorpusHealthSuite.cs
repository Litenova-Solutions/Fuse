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

        var repoTimeout = options.RepoTimeoutMinutes > 0
            ? TimeSpan.FromMinutes(options.RepoTimeoutMinutes)
            : (TimeSpan?)null;

        // Repositories present on disk with a discovered test suite, captured for the optional oracle-task pass.
        var taskable = new List<(CorpusRepo Repo, string Path, int TestProjects)>();

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

            string tier;
            string? tierNote = null;
            var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-health", Guid.NewGuid().ToString("N"), "fuse.db");
            try
            {
                // Restore and index are the stall-prone steps (a hung dotnet restore, a pathological
                // compilation). Wrap both in the per-repo hard timeout so one repo cannot wedge the sweep;
                // a timed-out repo is recorded and skipped, never silently dropped (D20).
                tier = await RunWithRepoTimeoutAsync(
                    async ct =>
                    {
                        if (options.Restore)
                            await manager.RestoreAsync(path, ct);
                        await using var store = new WorkspaceIndexStore(databasePath);
                        await store.InitializeAsync(ct);
                        return (await _indexer.IndexAsync(path, store, ct)).Mode;
                    },
                    repoTimeout,
                    cancellationToken);
            }
            catch (RepoTimeoutException)
            {
                tier = "timeout";
                tierNote = $"timed out after {options.RepoTimeoutMinutes} min (restore+index); skipped";
                notes.Add($"{repo.Name}: {tierNote}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tier = "error";
                tierNote = $"index error: {ex.Message}";
                notes.Add($"{repo.Name}: index error: {ex.Message}");
            }
            finally
            {
                TryDeleteStore(databasePath);
            }

            var (testProjects, testFiles) = DiscoverTests(path);
            health.Add(new CorpusRepoHealth(
                repo.Name, tier, Tier1: tier == "semantic", testProjects, testFiles,
                Note: tierNote ?? (testProjects == 0 ? "no test project discovered" : null)));

            if (testProjects > 0)
                taskable.Add((repo, path, testProjects));
        }

        // Oracle-task pass (C4 4b): mine and mechanically verify fail-to-pass tasks per repository. Each candidate
        // runs the changed tests at the base (with the tests applied) and the merge, so verification is real test
        // execution, not overlap. Bounded by the same per-repo hard timeout as the tier pass; a repo whose task
        // pass times out contributes the tasks verified so far and is recorded, never wedging the sweep (D20).
        var tasksTotal = 0;
        var tasksVerified = 0;
        // The verified tasks are persisted to results/corpus-tasks-v2.json (repo, base, merge, testFilter, title)
        // so the loop referendum (B1) can replay the exact set without re-mining and re-verifying (C4/B1).
        var verifiedRecords = new List<CorpusTaskRecord>();
        if (options.VerifyTasksPerRepo > 0)
        {
            var extractor = new CorpusTaskExtractor(manager);
            foreach (var (repo, path, _) in taskable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Per-repo budget as a linked deadline token passed INTO the verify loop, so a repo that runs long
                // (a large suite with slow tests) contributes the tasks it verified before the cutoff rather than
                // losing them to a thrown timeout. The outer token still aborts the whole sweep (D20).
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (repoTimeout is not null)
                    budget.CancelAfter(repoTimeout.Value);
                try
                {
                    var (attempted, verified) = await VerifyRepoTasksAsync(
                        extractor, path, repo.Name, options, notes, verifiedRecords, budget.Token, cancellationToken);
                    tasksTotal += attempted;
                    tasksVerified += verified;
                    options.Report($"corpus-health: {repo.Name} oracle tasks verified {verified}/{attempted}");
                    if (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        notes.Add($"{repo.Name}: oracle-task pass hit the {options.RepoTimeoutMinutes} min budget; {verified}/{attempted} recorded before the cutoff");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    notes.Add($"{repo.Name}: oracle-task pass error: {ex.Message}");
                }
            }

            // Write the replayable task set (even when empty: an honest record that the pass ran and found none).
            Directory.CreateDirectory(options.ResultsRoot);
            var taskSetPath = Path.Combine(options.ResultsRoot, CorpusTaskSet.FileName);
            var taskSet = new CorpusTaskSet(DateTime.UtcNow.ToString("O"), verifiedRecords);
            await File.WriteAllTextAsync(
                taskSetPath,
                JsonSerializer.Serialize(taskSet, BenchmarkJsonContext.Default.CorpusTaskSet),
                cancellationToken);
            options.Report($"corpus-health: wrote {taskSetPath} ({verifiedRecords.Count} verified tasks)");
            notes.Add($"persisted {verifiedRecords.Count} verified tasks to {CorpusTaskSet.FileName} (replayable by the loop suite)");
        }

        var report = new CorpusHealthReport(
            Generated: DateTime.UtcNow.ToString("O"),
            ReposTotal: repos.Count,
            ReposTier1: health.Count(h => h.Tier1),
            TasksTotal: tasksTotal,
            TasksVerified: tasksVerified,
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

        // Oracle-task verification runs only when --verify-tasks is set; without it the report records 0 verified
        // tasks (tier-only sweep) and MeetsMinimums stays false by construction. Recorded, not hidden.
        var taskNote = options.VerifyTasksPerRepo > 0
            ? $"verified oracle tasks {report.TasksVerified}/{report.TasksTotal} attempted"
            : "verified oracle tasks 0 (task pass not run; pass --verify-tasks N)";
        notes.Add($"tier-1 repos {report.ReposTier1}/{report.ReposTotal}; {taskNote}; meetsMinimums {report.MeetsMinimums} (needs >= {report.MinReposTier1} tier-1 repos and >= {report.MinTasksVerified} verified tasks).");

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

    // Mines candidate fail-to-pass tasks from one repository and verifies each mechanically, stopping once the
    // per-repo target is met or the candidate pool is exhausted. Returns the attempted and verified counts; a
    // per-repo verified task is added to the notes with its commit and title so the report is auditable.
    private static async Task<(int Attempted, int Verified)> VerifyRepoTasksAsync(
        CorpusTaskExtractor extractor,
        string path,
        string repoName,
        EvalOptions options,
        List<string> notes,
        List<CorpusTaskRecord> verifiedRecords,
        CancellationToken budgetToken,
        CancellationToken outerToken)
    {
        // Scan a wider candidate pool than the target, since many candidates will not verify (a test-only diff that
        // does not apply, a base that does not build, a flake, or a suite that needs an absent external service).
        var poolSize = Math.Max(options.VerifyTasksPerRepo * 4, options.VerifyTasksPerRepo + 4);
        var candidates = await extractor.MineCandidatesAsync(path, repoName, options.ScanCommits, poolSize, budgetToken);

        var attempted = 0;
        var verified = 0;
        foreach (var candidate in candidates)
        {
            if (verified >= options.VerifyTasksPerRepo)
                break;
            outerToken.ThrowIfCancellationRequested();
            // The per-repo budget stops the loop and RETURNS the partial counts (not throws), so a long-running
            // repo still contributes what it verified before the cutoff.
            if (budgetToken.IsCancellationRequested)
                break;
            attempted++;
            TaskVerification outcome;
            try
            {
                outcome = await extractor.VerifyAsync(path, candidate, budgetToken);
            }
            catch (OperationCanceledException) when (budgetToken.IsCancellationRequested && !outerToken.IsCancellationRequested)
            {
                attempted--; // This candidate did not complete; do not count the interrupted attempt.
                break;
            }
            if (outcome.Verified)
            {
                verified++;
                verifiedRecords.Add(new CorpusTaskRecord(
                    candidate.Repo, candidate.BaseCommit, candidate.Commit, candidate.TestFilter, candidate.Title,
                    candidate.TestFiles));
                var shortSha = candidate.Commit.Length >= 12 ? candidate.Commit[..12] : candidate.Commit;
                notes.Add($"{repoName}: oracle task {shortSha} \"{candidate.Title}\" verified ({outcome.Reason})");
            }
        }

        return (attempted, verified);
    }

    // Counts test projects (csproj on a test path) and test source files (*.cs on a test path) by scanning the
    // repository. A cheap structural signal that a repository has a runnable suite, ahead of the oracle run.
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

    /// <summary>
    ///     Runs a per-repository unit of work under an optional hard timeout. When <paramref name="timeout" /> is
    ///     null the work runs under the outer token unchanged. Otherwise a linked token cancels after the budget,
    ///     and if the work is cancelled by that budget (not by the outer token) a <see cref="RepoTimeoutException" />
    ///     is thrown so the caller can record the repository as timed out and continue the sweep (D20). Because the
    ///     external steps (dotnet restore, test) kill their process tree on cancellation, the budget is a real wall,
    ///     not merely a cooperative hint.
    /// </summary>
    /// <typeparam name="T">The work's result type.</typeparam>
    /// <param name="work">The work, given the (possibly time-bounded) token.</param>
    /// <param name="timeout">The per-repo budget, or null for no limit.</param>
    /// <param name="outer">The sweep's cancellation token.</param>
    /// <returns>The work's result.</returns>
    /// <exception cref="RepoTimeoutException">The budget elapsed before the work completed.</exception>
    /// <exception cref="OperationCanceledException">The outer token was cancelled.</exception>
    internal static async Task<T> RunWithRepoTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan? timeout,
        CancellationToken outer)
    {
        if (timeout is null)
            return await work(outer);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(timeout.Value);
        try
        {
            return await work(linked.Token);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            // The budget fired, not the outer sweep cancel: convert to a timeout so the sweep records and continues.
            throw new RepoTimeoutException();
        }
    }

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

/// <summary>
///     Signals that a corpus-health repository exceeded its per-repo hard timeout and was skipped (D20). Distinct
///     from <see cref="OperationCanceledException" /> so the sweep can tell a budget timeout (record and continue)
///     apart from an outer cancel (stop the sweep).
/// </summary>
public sealed class RepoTimeoutException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RepoTimeoutException" /> class.</summary>
    public RepoTimeoutException() : base("The repository exceeded its per-repo hard timeout.")
    {
    }
}
