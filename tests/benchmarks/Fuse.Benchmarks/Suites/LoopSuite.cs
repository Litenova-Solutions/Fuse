using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fuse.Semantics;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite R4: the loop metric. It measures what the oracle thesis actually moves, the number of build-gated
///     turns an agent takes to reach green (<see cref="LoopMetrics" />), not the per-payload token count that
///     Suite D showed does not change. One Claude Code CLI driver resolves each task (edit, build or
///     <c>fuse_check</c>, repeat) in two arms, <c>native</c> (filesystem plus <c>dotnet build/test</c>) and
///     <c>fuse</c> (the fuse MCP tools, so a verify can be a speculative <c>fuse_check</c> instead of a
///     <c>dotnet build</c> round-trip). The claim is that the fuse arm reaches green in fewer build-gated turns.
/// </summary>
/// <remarks>
///     Harness-first (the plan's R4 exception): the benchmark harness is the deliverable, and the numbers are
///     recorded when a model and the <c>claude</c> CLI are provisioned. The deterministic core, turn
///     classification (<see cref="LoopTranscriptClassifier" />) and the metric computation
///     (<see cref="LoopMetrics" />), is unit-tested and runs offline; the model-driven arms skip gracefully
///     (recording the arms and the curated task set, never a stub number) when the CLI is absent, exactly as
///     Suite D does.
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
    public string Description => "Loop metric (R4): build-gated turns to green per toolbox (model-dependent; harness is the deliverable).";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var model = options.AgentModel ?? DefaultModel;
        var notes = new List<string>
        {
            $"model {model}", $"max turns {MaxTurns}", "arms: native, fuse",
            "metric: build-gated turns to green (iterations_to_green), build invocations; lower is better",
            "harness-first: the harness is the deliverable; numbers are recorded when the claude CLI and a model are provisioned",
        };

        var fuseExe = Environment.ProcessPath;
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1");
        var present = dataset.Repos
            .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var perRepo = options.Limit > 0 ? options.Limit : 2;
        var wouldSample = present
            .SelectMany(r => r.Tasks
                .Where(t => t.GroundTruth.Files.Count > 0 && !SignalBucket.IsLowSignal(t.Category) && t.HeadRef is not null && t.BaseRef is not null)
                .Take(perRepo))
            .Select(t => t.Id)
            .ToList();

        // Execution is opt-in. A loop rollout drives the real claude CLI per task per arm, which costs model time
        // and minutes each; a bare `fuse eval loop` must never launch that silently (especially where the CLI is
        // present, as it is inside a Claude Code host). Without FUSE_LOOP_RUN the suite records the harness state
        // (arms, metric, and the task set it would sample) and stops. This is the harness-first deliverable; the
        // recorded numbers come from an explicit, provisioned run.
        if (!IsTruthy(Environment.GetEnvironmentVariable("FUSE_LOOP_RUN")))
        {
            notes.Add("execution opt-in required: set FUSE_LOOP_RUN=1 to run the model-driven arms.");
            notes.Add(ToolOnPath("claude") ? "claude CLI present." : "claude CLI not on PATH; a run also needs it.");
            notes.Add($"would sample {wouldSample.Count} PR(s): {string.Join(", ", wouldSample)}");
            return Skipped(notes);
        }

        if (!ToolOnPath("claude"))
        {
            notes.Add("FUSE_LOOP_RUN set but the claude CLI is not on PATH; the loop arms require it. Skipped (omit, never stub).");
            return Skipped(notes);
        }
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped.");
            return Skipped(notes);
        }
        var arms = new[] { "native", "fuse" };
        var tasks = new List<TaskResult>();
        var sampled = new List<string>();

        foreach (var repo in present)
        {
            var eligible = repo.Tasks
                .Where(t => t.GroundTruth.Files.Count > 0 && !SignalBucket.IsLowSignal(t.Category))
                .Take(perRepo)
                .ToList();
            foreach (var task in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (task.HeadRef is null || task.BaseRef is null)
                    continue;
                sampled.Add(task.Id);
                // Resolve from the base ref (the change not yet applied), so the agent has real work to do.
                var worktree = await manager.AddWorktreeAsync(repo.Path!, task.BaseRef, cancellationToken);
                if (worktree is null)
                {
                    options.Report($"loop: {task.Id} worktree failed; skipped");
                    continue;
                }

                if (options.Restore)
                    await manager.RestoreAsync(worktree, cancellationToken);

                try
                {
                    foreach (var arm in arms)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = await RunRolloutAsync(task, worktree, arm, model, fuseExe, options, cancellationToken);
                        if (row is not null)
                            tasks.Add(row);
                    }
                }
                finally
                {
                    await manager.RemoveWorktreeAsync(repo.Path!, worktree, cancellationToken);
                }
            }
        }

        if (tasks.Count == 0)
        {
            notes.Add("No rollouts produced a transcript; nothing scored.");
            return Skipped(notes);
        }

        notes.Add($"sampled PRs ({sampled.Count}): {string.Join(", ", sampled)}");
        AddArmNotes(tasks, notes);
        return Aggregate(tasks, notes);
    }

    private async Task<TaskResult?> RunRolloutAsync(
        PrTask task, string worktree, string arm, string model, string? fuseExe, EvalOptions options, CancellationToken ct)
    {
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
        if (arm == "fuse")
        {
            if (fuseExe is null)
                return null;
            argv.AddRange(["mcp__fuse", "Read", "Edit", "Write", "Bash"]);
            mcpConfigPath = WriteFuseMcpConfig(fuseExe);
            argv.AddRange(["--mcp-config", mcpConfigPath, "--strict-mcp-config"]);
        }
        else
        {
            argv.AddRange(["Read", "Grep", "Glob", "Edit", "Write", "Bash"]);
        }

        var transcript = await RunClaudeAsync(argv, worktree, ct);
        if (mcpConfigPath is not null)
            TryDelete(mcpConfigPath);
        if (transcript is null)
        {
            options.Report($"loop: {task.Id} {arm} wedged or empty; omitted");
            return null;
        }

        var turns = LoopTranscriptClassifier.Classify(transcript);
        var loop = LoopMetrics.Compute(turns);
        options.Report($"loop: {task.Id} {arm}: green {loop.ReachedGreen}, iters {loop.IterationsToGreen}, builds {loop.BuildInvocations}");

        // Recall column carries reached-green (1 or 0); tokens column carries iterations-to-green, so the
        // scorecard and per-task rows record the loop metric within the shared result shape.
        return new TaskResult(
            $"{task.Id}/{arm}", task.Repo, arm,
            loop.ReachedGreen ? 1.0 : 0.0,
            loop.BuildInvocations,
            loop.IterationsToGreen,
            loop.WallClockMs,
            new TaskFiles([], [], []));
    }

    private static void AddArmNotes(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        foreach (var arm in tasks.GroupBy(t => t.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var greenRate = Metrics.Mean(arm.Select(t => t.Recall).ToList());
            var medianIters = Metrics.Median(arm.Where(t => t.Recall > 0).Select(t => (double)t.Tokens));
            var meanBuilds = Metrics.Mean(arm.Select(t => t.Precision).ToList());
            notes.Add($"arm {arm.Key}: n {arm.Count()}, reached-green {greenRate:P0}, median iters-to-green {medianIters:N1}, mean build invocations {meanBuilds:N1}");
        }
    }

    private SuiteResult Aggregate(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        var fuse = tasks.Where(t => t.Category == "fuse").ToList();
        var green = Metrics.Mean((fuse.Count > 0 ? fuse : tasks).Select(t => t.Recall).ToList());
        var medianIters = Metrics.Median((fuse.Count > 0 ? fuse : tasks).Where(t => t.Recall > 0).Select(t => (double)t.Tokens));
        var scorecard = new Scorecard(tasks.Count, green, 0, 0, 0, 0, medianIters, medianIters);
        return new SuiteResult(Name, Description, null, scorecard, tasks, notes);
    }

    private SuiteResult Skipped(List<string> notes) =>
        new(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);

    private static string WriteFuseMcpConfig(string fuseExe)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-eval-loop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "fuse.mcp.json");
        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["fuse"] = new { command = fuseExe.Replace('\\', '/'), args = new[] { "mcp", "serve" }, timeout = 120_000 }
            }
        });
        File.WriteAllText(path, json);
        return path;
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
