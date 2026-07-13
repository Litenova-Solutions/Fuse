using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fuse.Semantics;

namespace Fuse.Benchmarks;

/// <summary>
///     One resolvable loop task: the minimum a rollout needs, decoupled from where the task came from. The loop
///     suite drives the same rollout over tasks sourced either from the persisted corpus-v2 verified oracle set
///     (B1) or, when that file is absent, the legacy <c>dotnet-prs-v1</c> dataset.
/// </summary>
/// <param name="Id">A stable task identifier (used in reporting and the resume checkpoint).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="RepoPath">The on-disk repository path a worktree is added from.</param>
/// <param name="BaseRef">The base commit the agent starts from (the change not yet applied).</param>
/// <param name="Title">The task title used as the agent prompt.</param>
/// <param name="MergeCommit">The merge commit whose gold tests the oracle post-check runs (empty for the legacy dataset path, which has no oracle).</param>
/// <param name="TestFilter">The <c>dotnet test --filter</c> for the gold tests (empty when no oracle).</param>
/// <param name="TestFiles">The gold test files checked out onto the agent's edit for the oracle post-check (empty when no oracle).</param>
internal sealed record LoopTask(
    string Id, string Repo, string RepoPath, string BaseRef, string Title,
    string MergeCommit, string TestFilter, IReadOnlyList<string> TestFiles)
{
    /// <summary>Whether this task carries a runnable fail-to-pass oracle (D22a): a merge commit, a filter, and gold test files.</summary>
    public bool HasOracle =>
        !string.IsNullOrWhiteSpace(MergeCommit) && !string.IsNullOrWhiteSpace(TestFilter) && TestFiles.Count > 0;
}

/// <summary>
///     Builds the loop suite's task list from either the persisted corpus-v2 verified oracle set (preferred) or
///     the legacy <c>dotnet-prs-v1</c> dataset (fallback). Pure and file-reading only, so the mapping is
///     unit-testable without a corpus on disk.
/// </summary>
internal static class LoopTaskSource
{
    /// <summary>
    ///     Reads <c>results/corpus-tasks-v2.json</c>, or returns null when it is absent, empty, or unparseable
    ///     (so the caller falls back to the legacy dataset).
    /// </summary>
    /// <param name="resultsRoot">The results directory.</param>
    /// <returns>The task set, or null.</returns>
    public static CorpusTaskSet? ReadCorpusTaskFile(string resultsRoot)
    {
        var path = Path.Combine(resultsRoot, CorpusTaskSet.FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            var set = JsonSerializer.Deserialize(File.ReadAllText(path), BenchmarkJsonContext.Default.CorpusTaskSet);
            return set is { Tasks.Count: > 0 } ? set : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Maps the persisted verified tasks to loop tasks, resolving each repository to an on-disk path and
    ///     dropping any repository that does not resolve (absent on disk).
    /// </summary>
    /// <param name="set">The persisted task set.</param>
    /// <param name="resolvePath">Resolves a repository name to its on-disk path, or null when absent.</param>
    /// <param name="repoFilter">An optional single-repository restriction, or null for all.</param>
    /// <param name="perRepoCap">A per-repository task cap (0 means all; the file is already curated).</param>
    /// <returns>The loop tasks.</returns>
    public static IReadOnlyList<LoopTask> FromCorpusTasks(
        CorpusTaskSet set, Func<string, string?> resolvePath, string? repoFilter, int perRepoCap)
    {
        var result = new List<LoopTask>();
        var byRepo = set.Tasks
            .Where(t => repoFilter is null || t.Repo.Equals(repoFilter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.Repo, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byRepo)
        {
            var path = resolvePath(group.Key);
            if (path is null)
                continue;
            var take = perRepoCap > 0 ? group.Take(perRepoCap) : group;
            foreach (var task in take)
            {
                var shortSha = task.BaseCommit.Length >= 8 ? task.BaseCommit[..8] : task.BaseCommit;
                result.Add(new LoopTask(
                    $"{task.Repo}@{shortSha}", task.Repo, path, task.BaseCommit, task.Title,
                    task.MergeCommit, task.TestFilter, task.TestFiles ?? []));
            }
        }

        return result;
    }

    /// <summary>
    ///     Maps the legacy <c>dotnet-prs-v1</c> dataset to loop tasks (the fallback when the corpus-v2 file is
    ///     absent): eligible, signal-bearing PR tasks with both refs, capped per repository.
    /// </summary>
    /// <param name="dataset">The dataset.</param>
    /// <param name="repoFilter">An optional single-repository restriction, or null for all.</param>
    /// <param name="perRepo">The per-repository task cap.</param>
    /// <returns>The loop tasks.</returns>
    public static IReadOnlyList<LoopTask> FromDataset(EvalDataset dataset, string? repoFilter, int perRepo)
    {
        var result = new List<LoopTask>();
        foreach (var repo in dataset.Repos
            .Where(r => r.Path is not null && (repoFilter is null || r.Name.Equals(repoFilter, StringComparison.OrdinalIgnoreCase))))
        {
            var eligible = repo.Tasks
                .Where(t => t.GroundTruth.Files.Count > 0 && !SignalBucket.IsLowSignal(t.Category)
                            && t.HeadRef is not null && t.BaseRef is not null)
                .Take(perRepo);
            foreach (var task in eligible)
                result.Add(new LoopTask(
                    task.Id, task.Repo, repo.Path!, task.BaseRef!, task.Title,
                    MergeCommit: "", TestFilter: "", TestFiles: [])); // legacy dataset: no fail-to-pass oracle
        }

        return result;
    }
}

/// <summary>
///     Suite R4/B1: the loop metric. It measures what the oracle thesis actually moves, the number of build-gated
///     turns an agent takes to reach green (<see cref="LoopMetrics" />), not the per-payload token count that
///     Suite D showed does not change. One Claude Code CLI driver resolves each task (edit, build or
///     <c>fuse_check</c>, repeat) in the <c>native</c> arm (filesystem plus <c>dotnet build/test</c>) and the
///     <c>fuse</c> arm (the Fuse MCP tools, so a verify can be a speculative <c>fuse_check</c> instead of a
///     <c>dotnet build</c> round-trip). An explicit environment option adds a <c>fuse-resident</c> arm whose
///     spawned MCP process receives <c>FUSE_RESIDENT=1</c>. The claim is that Fuse reaches green in fewer
///     build-gated turns.
/// </summary>
/// <remarks>
///     Harness-first (the plan's R4 exception): the benchmark harness is the deliverable, and the numbers are
///     recorded when a model and the <c>claude</c> CLI are provisioned. The deterministic core, turn
///     classification (<see cref="LoopTranscriptClassifier" />) and the metric computation
///     (<see cref="LoopMetrics" />), is unit-tested and runs offline; the model-driven arms skip gracefully
///     (recording the arms and the curated task set, never a stub number) when the CLI is absent, exactly as
///     Suite D does. A long run is interruptible: each completed rollout is written to a per-model checkpoint
///     (<see cref="LoopCheckpoint" />) so a resume continues where it stopped rather than restarting (B1).
/// </remarks>
public sealed class LoopSuite : IEvalSuite
{
    private const string DefaultModel = "claude-sonnet-4-6";
    private const int MaxTurns = 40;
    private const int RolloutTimeoutSec = 900;
    private readonly SemanticIndexer _indexer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LoopSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer (used to pre-build the fuse index for the fuse arm).</param>
    public LoopSuite(SemanticIndexer indexer) => _indexer = indexer;

    /// <inheritdoc />
    public string Name => "loop";

    /// <inheritdoc />
    public string Description => "Loop metric (R4/B1): build-gated turns to green per toolbox (model-dependent; harness is the deliverable).";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var model = options.AgentModel ?? DefaultModel;
        var shortStudy = IsTruthy(Environment.GetEnvironmentVariable("FUSE_LOOP_SHORT_STUDY"));
        var residentArm = IsTruthy(Environment.GetEnvironmentVariable("FUSE_LOOP_RESIDENT"));
        var arms = residentArm
            ? new[] { "native", "fuse", "fuse-resident" }
            : ["native", "fuse"];
        var notes = new List<string>
        {
            $"model {model}", $"max turns {MaxTurns}", $"arms: {string.Join(", ", arms)}",
            "metric: TRUE pass@1 from the gold-test oracle post-check (D22a), plus the reached-green proxy, "
            + "iterations-to-green, agent-visible build+test invocations counted apart from fuse_check turns, and false-done",
            "harness-first: the harness is the deliverable; numbers are recorded when the claude CLI and a model are provisioned",
        };
        if (residentArm)
            notes.Add("resident arm enabled: fuse-resident spawns its MCP server with FUSE_RESIDENT=1.");

        // C4 enforcement: a model-driven run must not start unless the corpus is proven healthy (a fresh,
        // passing corpus-health.json). Refuse and name the reason rather than spending model time on a corpus
        // that does not build.
        var gate = await CorpusHealthGate.CheckAsync(
            options.BenchRoot, options.ResultsRoot, options.ManifestPath, cancellationToken);
        if (!gate.Allowed)
        {
            notes.Add($"corpus-health gate: {gate.Reason}");
            return Skipped(notes);
        }

        if (gate.ReducedScope)
            notes.Add($"REDUCED-SCOPE run (no headline; confidence intervals only): {gate.Reason}");

        var fuseExe = Environment.ProcessPath;
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);

        // Prefer the persisted corpus-v2 verified oracle set (B1); fall back to the legacy dataset when absent.
        IReadOnlyList<LoopTask> loopTasks;
        var corpusSet = LoopTaskSource.ReadCorpusTaskFile(options.ResultsRoot);
        if (corpusSet is not null)
        {
            var manifest = manager.LoadManifest(options.ManifestPath);
            var repoByName = manifest.Repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            loopTasks = LoopTaskSource.FromCorpusTasks(
                corpusSet,
                name => repoByName.TryGetValue(name, out var repo) && manager.IsPresent(repo)
                    ? manager.ResolveRepoPath(repo)
                    : null,
                options.RepoFilter,
                shortStudy ? 0 : options.Limit);
            notes.Add($"task source: {CorpusTaskSet.FileName} ({corpusSet.Tasks.Count} verified tasks, generated {corpusSet.Generated})");
        }
        else
        {
            var dataset = manager.LoadDataset("dotnet-prs-v1");
            var perRepo = shortStudy ? int.MaxValue : options.Limit > 0 ? options.Limit : 2;
            loopTasks = LoopTaskSource.FromDataset(dataset, options.RepoFilter, perRepo);
            notes.Add($"task source: dotnet-prs-v1 (fallback; {CorpusTaskSet.FileName} absent)");
        }

        if (shortStudy)
        {
            if (options.Limit <= 0)
            {
                notes.Add("short-study mode requires --limit N with N greater than zero; skipped.");
                return Skipped(notes);
            }

            loopTasks = loopTasks.Take(options.Limit).ToList();
            notes.Add($"SHORT STUDY: deterministic total-task cap {options.Limit}; one rollout per arm; no headline or significance claim.");
        }

        // Execution is opt-in. A loop rollout drives the real claude CLI per task per arm, which costs model time
        // and minutes each; a bare `fuse eval loop` must never launch that silently (especially where the CLI is
        // present, as it is inside a Claude Code host). Without FUSE_LOOP_RUN the suite records the harness state
        // (arms, metric, and the task set it would sample) and stops. This is the harness-first deliverable; the
        // recorded numbers come from an explicit, provisioned run.
        if (!IsTruthy(Environment.GetEnvironmentVariable("FUSE_LOOP_RUN")))
        {
            notes.Add("execution opt-in required: set FUSE_LOOP_RUN=1 to run the model-driven arms.");
            notes.Add(ToolOnPath("claude") ? "claude CLI present." : "claude CLI not on PATH; a run also needs it.");
            notes.Add($"would sample {loopTasks.Count} task(s): {string.Join(", ", loopTasks.Select(t => t.Id))}");
            return Skipped(notes);
        }

        if (!ToolOnPath("claude"))
        {
            notes.Add("FUSE_LOOP_RUN set but the claude CLI is not on PATH; the loop arms require it. Skipped (omit, never stub).");
            return Skipped(notes);
        }

        notes.Add($"claude CLI version {await ClaudeVersionAsync(cancellationToken)}");

        if (loopTasks.Count == 0)
        {
            notes.Add("No loop tasks resolved (corpus absent or no eligible tasks); skipped.");
            return Skipped(notes);
        }

        // Resume: load the per-model checkpoint so an interrupted long run continues where it stopped. A
        // checkpoint written under a different model is ignored (never mix rollouts across models).
        var checkpointVariant = shortStudy
            ? $"short-{options.Limit}{(residentArm ? "-resident" : string.Empty)}"
            : residentArm ? "resident" : null;
        var checkpointPath = Path.Combine(options.ResultsRoot, CheckpointFileName(model, checkpointVariant));
        var (done, tasks) = LoadCheckpoint(checkpointPath, model);
        if (tasks.Count > 0)
            notes.Add($"resumed from checkpoint: {tasks.Count} rollout(s) already recorded ({checkpointPath})");

        // Rollouts per arm (D22b): each arm runs this many independent rollouts per task, so a headline run can
        // average over the model's variance (2 per arm per task for the B1 re-run) rather than a single draw.
        var rolloutsPerArm = shortStudy ? 1 : Math.Max(1, options.Rollouts);
        notes.Add($"rollouts per arm: {rolloutsPerArm}");

        var wedged = 0;
        var sampled = new List<string>();
        var rolloutFailures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in loopTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = (from arm in arms
                           from r in Enumerable.Range(1, rolloutsPerArm)
                           let id = $"{task.Id}/{arm}#{r}"
                           where !done.Contains(id)
                           select (arm, r, id)).ToList();
            if (pending.Count == 0)
                continue; // Every arm/rollout already recorded for this task; skip on resume.

            sampled.Add(task.Id);

            // Each rollout runs on its OWN fresh worktree checked out at the base ref (D22a). A shared worktree
            // would let one rollout's edits leak into the next and make the per-rollout oracle post-check
            // meaningless; a fresh worktree per rollout gives each an identical clean start.
            foreach (var (arm, rollout, rowId) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var worktree = await manager.AddWorktreeAsync(task.RepoPath, task.BaseRef, cancellationToken);
                if (worktree is null)
                {
                    options.Report($"loop: {rowId} worktree failed; skipped");
                    continue;
                }

                try
                {
                    if (options.Restore)
                        await manager.RestoreAsync(worktree, cancellationToken);

                    var row = await RunRolloutAsync(
                        task, worktree, arm, rollout, model, fuseExe, options, rolloutFailures, cancellationToken);
                    if (row is null)
                    {
                        wedged++;
                        continue; // Wedged or empty transcript: omitted and counted, never stubbed (B1).
                    }

                    tasks.Add(row);
                    done.Add(row.Id);
                    await SaveCheckpointAsync(checkpointPath, model, tasks, cancellationToken);
                }
                finally
                {
                    await manager.RemoveWorktreeAsync(task.RepoPath, worktree, cancellationToken);
                }
            }
        }

        if (tasks.Count == 0)
        {
            notes.AddRange(rolloutFailures.Order(StringComparer.Ordinal));
            notes.Add("No rollouts produced a transcript; nothing scored.");
            return Skipped(notes);
        }

        notes.AddRange(rolloutFailures.Order(StringComparer.Ordinal));
        notes.Add($"tasks {loopTasks.Count}; rollouts recorded {tasks.Count}; wedged/omitted this run {wedged}");
        notes.Add($"sampled this run ({sampled.Count}): {string.Join(", ", sampled)}");
        AddArmNotes(tasks, notes);
        return Aggregate(tasks, notes);
    }

    private async Task<TaskResult?> RunRolloutAsync(
        LoopTask task,
        string worktree,
        string arm,
        int rollout,
        string model,
        string? fuseExe,
        EvalOptions options,
        ISet<string> rolloutFailures,
        CancellationToken ct)
    {
        var rowId = $"{task.Id}/{arm}#{rollout}";
        var prompt =
            $"Implement this change in this repository: {task.Title}. " +
            "Make the edits, then verify your change compiles (and passes tests where relevant) before you stop. " +
            "Iterate until the build is green.";

        var argv = new List<string>
        {
            "-p", prompt, "--model", model, "--output-format", "stream-json", "--verbose",
            "--permission-mode", "acceptEdits", "--max-turns", MaxTurns.ToString(), "--allowedTools"
        };

        string? mcpConfigPath = null;
        if (arm is "fuse" or "fuse-resident")
        {
            if (fuseExe is null)
                return null;
            argv.AddRange(["mcp__fuse", "Read", "Edit", "Write", "Bash"]);
            mcpConfigPath = WriteFuseMcpConfig(fuseExe, resident: arm == "fuse-resident");
            argv.AddRange(["--mcp-config", mcpConfigPath, "--strict-mcp-config"]);
        }
        else
        {
            argv.AddRange(["Read", "Grep", "Glob", "Edit", "Write", "Bash"]);
        }

        var rolloutWatch = Stopwatch.StartNew();
        var transcript = await RunClaudeAsync(argv, worktree, ct);
        rolloutWatch.Stop();
        if (mcpConfigPath is not null)
            TryDelete(mcpConfigPath);
        if (transcript is null)
        {
            options.Report($"loop: {rowId} wedged or empty; omitted");
            return null;
        }

        // Retain the raw transcript (D22a): the classifier is auditable and a disputed rollout can be re-scored.
        await SaveTranscriptAsync(options.ResultsRoot, rowId, transcript, ct);
        if (transcript.Contains("\"error\":\"authentication_failed\"", StringComparison.Ordinal))
        {
            const string failure = "rollout blocker: model driver authentication failed; fix the Claude CLI credential before rerunning.";
            rolloutFailures.Add(failure);
            options.Report($"loop: {rowId} authentication failed; omitted");
            return null;
        }

        var turns = LoopTranscriptClassifier.Classify(transcript);
        // Transcript events carry no reliable per-turn duration. The canonical latency is the measured driver
        // process duration, which includes model generation and every tool round-trip in the rollout.
        var loop = LoopMetrics.Compute(turns, Math.Max(1, rolloutWatch.ElapsedMilliseconds));

        // The oracle post-check (D22a): run the task's gold tests over the agent's finished edit for a TRUE
        // pass@1 and to expose false-done, independent of what the transcript claimed. Skipped (null) when the
        // task carries no persisted gold tests (the legacy dataset, or a pre-D22a task file).
        var oracleOutcome = task.HasOracle ? await RunOracleAsync(task, worktree, ct) : null;
        var verdict = OraclePostCheck.Decide(loop.ReachedGreen, oracleOutcome);

        options.Report(
            $"loop: {rowId}: proxy-green {loop.ReachedGreen}, iters {loop.IterationsToGreen}, "
            + $"builds {loop.BuildInvocations}, checks {loop.CheckInvocations}, tests {loop.TestInvocations}, "
            + $"rollout {loop.WallClockMs} ms, "
            + $"oracle {(verdict.OraclePassed is null ? "na" : verdict.OraclePassed.Value ? "pass" : "fail")}"
            + $"{(verdict.FalseDone ? " FALSE-DONE" : string.Empty)}");

        // Column mapping within the shared result shape: Recall = reached-green proxy (1/0), Precision =
        // agent-visible build+test round-trips (no longer folding fuse_check), Tokens = iterations-to-green,
        // Checks = fuse_check turns, OraclePassed = true pass@1, FalseDone = proxy-green-but-oracle-red (D22a).
        return new TaskResult(
            rowId, task.Repo, arm,
            loop.ReachedGreen ? 1.0 : 0.0,
            loop.AgentVisibleVerifications,
            loop.IterationsToGreen,
            loop.WallClockMs,
            new TaskFiles([], [], []),
            loop.CheckInvocations,
            verdict.OraclePassed,
            verdict.FalseDone);
    }

    // The oracle post-check runner (D22a): check the task's gold test files out of the merge commit onto the
    // agent's edited worktree (so the agent's source change meets the fail-to-pass tests), then run just those
    // tests. A build or runner failure returns did-not-execute, which the decision treats as unscored, never a
    // pass. Best-effort git/test plumbing; the decision logic itself is pure and unit-tested.
    private static async Task<TestRunOutcome?> RunOracleAsync(LoopTask task, string worktree, CancellationToken ct)
    {
        try
        {
            var checkout = await GitCli.RunAsync(
                worktree, ct, ["checkout", task.MergeCommit, "--", .. task.TestFiles]);
            if (!checkout.Ok)
                return TestRunOutcome.DidNotExecute; // Could not materialize the gold tests; not scorable.

            return await TaskOracle.RunDotnetTestAsync(worktree, task.TestFilter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TestRunOutcome.DidNotExecute;
        }
    }

    // Writes the raw stream-json transcript under results/loop-transcripts so a rollout is auditable and
    // re-scorable (D22a). Best-effort: a failed write never aborts a costly run.
    private static async Task SaveTranscriptAsync(string resultsRoot, string rowId, string transcript, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(resultsRoot, "loop-transcripts");
            Directory.CreateDirectory(dir);
            var safe = new string(rowId.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            await File.WriteAllTextAsync(Path.Combine(dir, $"{safe}.jsonl"), transcript, ct);
        }
        catch (IOException)
        {
            // A missing transcript only costs auditability of that rollout; never fail the run over it.
        }
    }

    // Loads the per-model resume checkpoint: the set of already-recorded rollout ids and the recorded results.
    // A checkpoint written under a different model is ignored so rollouts are never mixed across models.
    private static (HashSet<string> Done, List<TaskResult> Tasks) LoadCheckpoint(string path, string model)
    {
        if (!File.Exists(path))
            return (new HashSet<string>(StringComparer.Ordinal), []);
        try
        {
            var checkpoint = JsonSerializer.Deserialize(File.ReadAllText(path), BenchmarkJsonContext.Default.LoopCheckpoint);
            if (checkpoint is null || !checkpoint.Model.Equals(model, StringComparison.Ordinal))
                return (new HashSet<string>(StringComparer.Ordinal), []);
            var tasks = checkpoint.Rollouts.ToList();
            return (tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal), tasks);
        }
        catch (JsonException)
        {
            return (new HashSet<string>(StringComparer.Ordinal), []);
        }
    }

    // Rewrites the whole checkpoint after each rollout (the set is small; a full rewrite is atomic enough and
    // avoids partial-line corruption). Best-effort: a failed checkpoint write must not abort a costly run.
    private static async Task SaveCheckpointAsync(string path, string model, IReadOnlyList<TaskResult> tasks, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null)
                Directory.CreateDirectory(directory);
            var checkpoint = new LoopCheckpoint(model, tasks);
            await File.WriteAllTextAsync(
                path, JsonSerializer.Serialize(checkpoint, BenchmarkJsonContext.Default.LoopCheckpoint), ct);
        }
        catch (IOException)
        {
            // A missed checkpoint only costs a re-run of that rollout; never fail the run over it.
        }
    }

    // A filename-safe per-model checkpoint name. Short and resident studies use distinct files so their arm/task
    // sets cannot silently mix with the pre-registered run.
    private static string CheckpointFileName(string model, string? variant)
    {
        var safe = new string(model.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return variant is null ? $"loop-rollouts-{safe}.json" : $"loop-rollouts-{safe}-{variant}.json";
    }

    private static void AddArmNotes(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        var timing = LoopTimingMetrics.SummarizeArms(tasks);
        var zeroDurations = tasks.Count(task => task.LatencyMs <= 0);
        foreach (var arm in tasks.GroupBy(t => t.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var greens = arm.Select(t => t.Recall).ToList();
            var greenRate = Metrics.Mean(greens);
            var (ciLow, ciHigh) = Metrics.BootstrapCi(greens);
            var medianIters = Metrics.Median(arm.Where(t => t.Recall > 0).Select(t => (double)t.Tokens));
            var meanBuilds = Metrics.Mean(arm.Select(t => t.Precision).ToList());
            var meanChecks = Metrics.Mean(arm.Select(t => (double)t.Checks).ToList());
            notes.Add($"arm {arm.Key}: n {arm.Count()}, reached-green (proxy) {greenRate:P0} (95% CI {ciLow:P0}-{ciHigh:P0}), median iters-to-green {medianIters:N1}, mean agent-visible build+test invocations {meanBuilds:N1}, mean fuse_check turns {meanChecks:N1}");
            var armTiming = timing.SingleOrDefault(summary => summary.Arm == arm.Key);
            if (armTiming is not null)
                notes.Add($"arm {arm.Key}: elapsed n {armTiming.RolloutCount} rollouts across {armTiming.TaskCount} tasks, total {armTiming.TotalMs / 1000.0:N1}s, median rollout {armTiming.MedianRolloutMs / 1000.0:N1}s, task-clustered mean {armTiming.MeanTaskMs / 1000.0:N1}s and median {armTiming.MedianTaskMs / 1000.0:N1}s.");
            else
                notes.Add($"arm {arm.Key}: no positive rollout durations recorded.");

            // True pass@1 and false-done from the oracle post-check (D22a), over the rollouts the oracle could
            // score (an unscored rollout - no gold tests, or a gold run that did not build - is excluded from the
            // denominator, never counted as a pass or a fail).
            var scored = arm.Where(t => t.OraclePassed is not null).ToList();
            if (scored.Count > 0)
            {
                var passes = scored.Select(t => t.OraclePassed!.Value ? 1.0 : 0.0).ToList();
                var (pLow, pHigh) = Metrics.BootstrapCi(passes);
                var falseDone = arm.Count(t => t.FalseDone);
                notes.Add($"arm {arm.Key}: oracle-scored {scored.Count}/{arm.Count()}, TRUE pass@1 {Metrics.Mean(passes):P0} (95% CI {pLow:P0}-{pHigh:P0}), false-done {falseDone}");
            }
            else
            {
                notes.Add($"arm {arm.Key}: oracle post-check not run for any rollout (no gold tests persisted; regenerate corpus-tasks-v2.json with D22a to score true pass@1).");
            }
        }

        if (zeroDurations > 0)
            notes.Add($"elapsed-time summaries excluded {zeroDurations} rollout(s) with zero latency from older checkpoints.");

        foreach (var arm in timing.Where(summary => summary.Arm != "native"))
        {
            var paired = LoopTimingMetrics.ComparePaired(tasks, "native", arm.Arm);
            notes.Add($"elapsed native vs {arm.Arm}: {paired.PairedTaskCount} paired tasks; task-clustered delta ({arm.Arm} minus native) mean {paired.MeanDeltaMs / 1000.0:N1}s, median {paired.MedianDeltaMs / 1000.0:N1}s; descriptive only, no significance claim.");
            var verified = LoopTimingMetrics.ComparePairedVerified(tasks, "native", arm.Arm);
            notes.Add($"verified elapsed native vs {arm.Arm}: {verified.VerifiedPairCount} paired tasks passed the gold-test oracle in both arms; median time saving {verified.MedianRelativeSavings:P1}, mean delta {verified.MeanDeltaMs / 1000.0:N1}s, median delta {verified.MedianDeltaMs / 1000.0:N1}s; use this speed estimate only with the separately reported pass rates and sample size.");
        }
    }

    private SuiteResult Aggregate(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        var fuse = tasks.Where(t => t.Category == "fuse").ToList();
        var scored = fuse.Count > 0 ? fuse : tasks;
        var greens = scored.Select(t => t.Recall).ToList();
        var green = Metrics.Mean(greens);
        var (ciLow, ciHigh) = Metrics.BootstrapCi(greens);
        var medianIters = Metrics.Median(scored.Where(t => t.Recall > 0).Select(t => (double)t.Tokens));
        var scorecard = new Scorecard(tasks.Count, green, ciLow, ciHigh, 0, 0, medianIters, medianIters);
        return new SuiteResult(Name, Description, null, scorecard, tasks, notes);
    }

    private SuiteResult Skipped(List<string> notes) =>
        new(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);

    private static string WriteFuseMcpConfig(string fuseExe, bool resident)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-eval-loop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "fuse.mcp.json");
        object server = resident
            ? new
            {
                command = fuseExe.Replace('\\', '/'),
                args = new[] { "mcp", "serve" },
                timeout = 120_000,
                env = new Dictionary<string, string> { ["FUSE_RESIDENT"] = "1" },
            }
            : new
            {
                command = fuseExe.Replace('\\', '/'),
                args = new[] { "mcp", "serve" },
                timeout = 120_000,
            };
        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["fuse"] = server
            }
        });
        File.WriteAllText(path, json);
        return path;
    }

    // Records the driver CLI version alongside the numbers (B1 requires the model and CLI versions pinned and
    // recorded). Best-effort: returns "unknown" if the version cannot be read.
    private static async Task<string> ClaudeVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("claude")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var text = stdout.Trim();
            return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "unknown";
        }
    }

    private static async Task<string?> RunClaudeAsync(IReadOnlyList<string> argv, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("claude")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in argv)
            psi.ArgumentList.Add(a);
        psi.Environment["MCP_TOOL_TIMEOUT"] = "120000";

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, _) => { };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(RolloutTimeoutSec));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }

        var text = stdout.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static bool ToolOnPath(string tool)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var candidates = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool + ".cmd", tool } : [tool];
        return paths.Any(dir =>
        {
            try { return candidates.Any(c => File.Exists(Path.Combine(dir, c))); }
            catch { return false; }
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }
}
