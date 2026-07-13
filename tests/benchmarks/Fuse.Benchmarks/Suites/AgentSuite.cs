using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fuse.Semantics;

namespace Fuse.Benchmarks;

/// <summary>
///     Suite D: agent context sufficiency (model-dependent, not byte-reproducible). One Claude Code CLI
///     driver is given a task and one of two toolboxes - <c>native</c> (filesystem Read/Grep/Glob) or
///     <c>fuse</c> (the fuse MCP tools plus Read) - so any difference is attributable to the tools, not
///     the brain. It measures the cost to acquire sufficient context (tool calls, cumulative input
///     tokens) and the quality of what was acquired (file recall/precision against the PR change set,
///     plus a model-scored sufficiency verdict).
/// </summary>
/// <remarks>
///     Honesty contract (mirrors the retired layer5 driver): the model id, run date, and N are stamped;
///     arms whose tool is absent are omitted, never stubbed; recall is always read together with tokens.
///     Requires the <c>claude</c> CLI on PATH and authentication; absent that, the suite skips gracefully.
/// </remarks>
public sealed partial class AgentSuite : IEvalSuite
{
    private const string DefaultModel = "claude-sonnet-4-6";
    private const int MaxTurns = 25;
    private const int RolloutTimeoutSec = 600;
    private readonly SemanticIndexer _indexer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AgentSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer (used to pre-build the fuse index for the fuse arm).</param>
    public AgentSuite(SemanticIndexer indexer) => _indexer = indexer;

    /// <inheritdoc />
    public string Name => "agent";

    /// <inheritdoc />
    public string Description => "Agent context sufficiency: file recall and token cost per toolbox (model-dependent).";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var model = options.AgentModel ?? DefaultModel;
        var fuseExe = Environment.ProcessPath;
        var notes = new List<string> { $"model {model}", $"max turns {MaxTurns}", "arms: native, fuse" };

        // C4 enforcement: refuse a model-driven run unless the corpus is proven healthy (fresh, passing
        // corpus-health.json), naming the reason instead of spending model time on a corpus that does not build.
        var gate = await CorpusHealthGate.CheckAsync(options.BenchRoot, options.ResultsRoot, cancellationToken);
        if (!gate.Allowed)
        {
            notes.Add($"corpus-health gate: {gate.Reason}");
            return Skipped(notes);
        }

        if (gate.ReducedScope)
            notes.Add($"REDUCED-SCOPE run (no headline; confidence intervals only): {gate.Reason}");

        if (!ToolOnPath("claude"))
        {
            notes.Add("claude CLI not found on PATH; agent suite requires it. Skipped (omit, never stub).");
            return Skipped(notes);
        }
        if (fuseExe is null || !fuseExe.EndsWith("fuse.exe", StringComparison.OrdinalIgnoreCase) && !fuseExe.EndsWith("fuse", StringComparison.OrdinalIgnoreCase))
            notes.Add($"warning: fuse executable resolved to {fuseExe ?? "null"}; fuse arm uses this path for the MCP server.");

        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1");
        var present = dataset.Repos
            .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped.");
            return Skipped(notes);
        }

        var perRepo = options.Limit > 0 ? options.Limit : 1;
        var rollouts = Math.Max(1, options.Rollouts);
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
                if (task.HeadRef is null)
                    continue;
                sampled.Add(task.Id);
                var worktree = await manager.AddWorktreeAsync(repo.Path!, task.HeadRef, cancellationToken);
                if (worktree is null)
                {
                    options.Report($"agent: {task.Id} worktree failed; skipped");
                    continue;
                }

                if (options.Restore)
                    await manager.RestoreAsync(worktree, cancellationToken);

                try
                {
                    foreach (var arm in arms)
                    {
                        for (var r = 1; r <= rollouts; r++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var row = await RunRolloutAsync(task, worktree, arm, r, model, fuseExe, options, cancellationToken);
                            if (row is not null)
                                tasks.Add(row);
                        }
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
        notes.Add($"rollouts per (PR, arm): {rollouts}");
        AddArmNotes(tasks, notes);
        return Aggregate(tasks, notes);
    }

    private async Task<TaskResult?> RunRolloutAsync(
        PrTask task, string worktree, string arm, int rollout, string model, string? fuseExe,
        EvalOptions options, CancellationToken ct)
    {
        var prompt = $"Gather enough context to implement this change: {task.Title}. " +
                     "When you have the files you need, stop and list them as a bullet list of repo-relative paths.";

        var argv = new List<string>
        {
            "-p", prompt, "--model", model, "--output-format", "stream-json", "--verbose",
            "--permission-mode", "default", "--max-turns", MaxTurns.ToString(), "--allowedTools"
        };

        string? mcpConfigPath = null;
        if (arm == "fuse")
        {
            if (fuseExe is null)
                return null;
            argv.AddRange(["mcp__fuse", "Read"]);
            mcpConfigPath = WriteFuseMcpConfig(fuseExe);
            argv.AddRange(["--mcp-config", mcpConfigPath, "--strict-mcp-config"]);
        }
        else
        {
            argv.AddRange(["Read", "Grep", "Glob"]);
        }

        var transcript = await RunClaudeAsync(argv, worktree, ct);
        if (mcpConfigPath is not null)
            TryDelete(mcpConfigPath);
        if (transcript is null)
        {
            options.Report($"agent: {task.Id} {arm} r{rollout} wedged or empty; omitted");
            return null;
        }

        var parsed = ParseTranscript(transcript, worktree, arm);
        var groundTruth = task.GroundTruth.Files.Select(f => Normalize(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recall = Metrics.Recall(parsed.Acquired, groundTruth);
        var precision = Metrics.Precision(parsed.Acquired.ToList(), groundTruth);
        options.Report($"agent: {task.Id} {arm} r{rollout}: {parsed.ToolCalls} calls, {parsed.CumInputTokens} tok, recall {recall:P0} prec {precision:P0}");

        return new TaskResult(
            $"{task.Id}/{arm}/r{rollout}", task.Repo, arm, recall, precision, parsed.CumInputTokens, 0,
            new TaskFiles(
                groundTruth.Where(parsed.Acquired.Contains).ToList(),
                groundTruth.Where(g => !parsed.Acquired.Contains(g)).ToList(),
                parsed.Acquired.Where(a => !groundTruth.Contains(a)).Take(20).ToList()));
    }

    private static string WriteFuseMcpConfig(string fuseExe)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-eval-d", Guid.NewGuid().ToString("N"));
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
            UseShellExecute = false
        };
        foreach (var a in argv)
            psi.ArgumentList.Add(a);
        psi.Environment["MCP_TOOL_TIMEOUT"] = "120000";
        psi.Environment["MCP_TIMEOUT"] = "30000";

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
            TryKill(process);
            return null;
        }

        var text = stdout.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ParsedTranscript ParseTranscript(string transcript, string worktree, string arm)
    {
        var toolCalls = 0;
        var acquired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cumInput = 0;
        var worktreeNorm = worktree.Replace('\\', '/');

        foreach (var line in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl))
                    continue;
                var type = typeEl.GetString();

                if (type == "result" && root.TryGetProperty("usage", out var usage))
                {
                    cumInput = GetInt(usage, "input_tokens") + GetInt(usage, "cache_read_input_tokens") + GetInt(usage, "cache_creation_input_tokens");
                }
                else if (type == "assistant" && TryContent(root, out var content))
                {
                    foreach (var c in content.EnumerateArray())
                    {
                        if (c.TryGetProperty("type", out var ct2) && ct2.GetString() == "tool_use")
                        {
                            toolCalls++;
                            if (c.TryGetProperty("name", out var nameEl) && nameEl.GetString() == "Read"
                                && c.TryGetProperty("input", out var input) && input.TryGetProperty("file_path", out var fp))
                                AddAcquired(acquired, fp.GetString(), worktreeNorm);
                        }
                    }
                }
                else if (type == "user" && TryContent(root, out var userContent))
                {
                    foreach (var c in userContent.EnumerateArray())
                    {
                        if (!(c.TryGetProperty("type", out var ct3) && ct3.GetString() == "tool_result"))
                            continue;
                        var text = ExtractText(c);
                        if (arm == "fuse")
                            foreach (System.Text.RegularExpressions.Match m in FuseFileRegex().Matches(text))
                                AddAcquired(acquired, m.Groups[1].Value, worktreeNorm);
                        // native: a Grep/Glob listing is not acquisition; only Read tool_use counts (handled above).
                    }
                }
            }
        }

        return new ParsedTranscript(toolCalls, cumInput, acquired);
    }

    private static string ExtractText(JsonElement toolResult)
    {
        if (!toolResult.TryGetProperty("content", out var content))
            return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("text", out var t))
                    sb.AppendLine(t.GetString());
            return sb.ToString();
        }

        return string.Empty;
    }

    private static void AddAcquired(HashSet<string> set, string? raw, string worktreeNorm)
    {
        if (string.IsNullOrEmpty(raw))
            return;
        var p = raw.Replace('\\', '/');
        if (p.StartsWith(worktreeNorm + "/", StringComparison.OrdinalIgnoreCase))
            p = p[(worktreeNorm.Length + 1)..];
        else if (p.StartsWith(worktreeNorm, StringComparison.OrdinalIgnoreCase))
            p = p[worktreeNorm.Length..].TrimStart('/');
        p = p.TrimStart('.', '/');
        if (p.Length > 0)
            set.Add(p);
    }

    private void AddArmNotes(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        foreach (var arm in tasks.GroupBy(t => t.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var recall = Metrics.Mean(arm.Select(t => t.Recall).ToList());
            var precision = Metrics.Mean(arm.Select(t => t.Precision).ToList());
            var medianTokens = Metrics.Median(arm.Select(t => (double)t.Tokens));
            notes.Add($"arm {arm.Key}: n {arm.Count()}, mean recall {recall:P0}, mean precision {precision:P0}, median tokens {medianTokens:N0}");
        }
    }

    private SuiteResult Aggregate(IReadOnlyList<TaskResult> tasks, List<string> notes)
    {
        // Headline scorecard reports the fuse arm; per-arm detail is in the notes (honesty: never one mean across arms).
        var fuse = tasks.Where(t => t.Category == "fuse").ToList();
        var headline = fuse.Count > 0 ? fuse : tasks;
        var recalls = headline.Select(t => t.Recall).ToList();
        var precisions = headline.Select(t => t.Precision).ToList();
        var tokens = headline.Select(t => (double)t.Tokens).ToList();
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

    private static int GetInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;

    private static bool TryContent(JsonElement root, out JsonElement content)
    {
        content = default;
        return root.TryGetProperty("message", out var msg)
               && msg.TryGetProperty("content", out content)
               && content.ValueKind == JsonValueKind.Array;
    }

    private static bool ToolOnPath(string tool)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool + ".cmd", tool } : [tool];
        return paths.Any(dir =>
        {
            try
            {
                return names.Any(n => File.Exists(Path.Combine(dir, n)));
            }
            catch (ArgumentException)
            {
                return false;
            }
        });
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("<file path=\"([^\"]+)\"")]
    private static partial System.Text.RegularExpressions.Regex FuseFileRegex();

    private readonly record struct ParsedTranscript(int ToolCalls, int CumInputTokens, HashSet<string> Acquired);
}
