using System.Diagnostics;

namespace Fuse.Evals;

/// <summary>
///     Whether a fixed post-edit error is delivered again: an error introduced and reported through the Claude Code
///     post-edit hook, fixed without another hook, and asked about by the stop hook. Stale is an error the stop delivers
///     that the current tree no longer has. No truth and no test runs.
/// </summary>
internal static class HookStaleness
{
    /// <summary>The name the breaking statement uses, which nothing declares, so its error names it and no other error does.</summary>
    private const string MissingName = "fuseEvalMissingName";

    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var build = await repo.BuildAsync();
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", build.Errors.Take(3))}");
        var preferred = Path.Combine(repo.Root, "src", "Core", "Geometry.cs");
        var file = File.Exists(preferred) && Editable(preferred)
            ? preferred
            : solution.CodeSources().FirstOrDefault(Editable)
            ?? throw new InvalidOperationException($"the stale scenario needs a file with a method body; found none in {solution.Root}");
        var bytes = File.ReadAllBytes(file);
        var original = File.ReadAllText(file);
        try
        {
            await File.WriteAllTextAsync(file, MultiAgentScenario.TextEdit(original, MissingName));
            await Task.Delay(500);
            var (postEdit, postEditMs) = await HookAsync(repo, "post-edit", PostEditPayload(repo, file));
            var postEditErrors = EvalRepo.ParseFuseErrors(postEdit);

            var broken = await File.ReadAllTextAsync(file);
            if (!broken.Contains("_ = fuseEvalMissingName;", StringComparison.Ordinal))
                throw new InvalidOperationException("the stale scenario's break did not land");
            await File.WriteAllTextAsync(file, broken.Replace("_ = fuseEvalMissingName;", "var fuseEvalMissingName = 0;", StringComparison.Ordinal));
            await Task.Delay(500);
            var check = await repo.FuseTimedAsync("check", file);
            var checkErrors = EvalRepo.ParseFuseErrors(check.Output);

            var (stop, stopMs) = await HookAsync(repo, "stop", StopPayload(repo));
            var (blocked, reason) = EvalRepo.ParseStopBlock(stop);
            var stopErrors = EvalRepo.ParseFuseErrors(reason);

            var current = EvalRepo.ParseFuseErrors((await repo.FuseAsync("check")).Result.Output);
            var currentKeys = current.Select(CorrectnessSuite.Key).ToHashSet(StringComparer.Ordinal);
            var stale = stopErrors.Where(e => !currentKeys.Contains(CorrectnessSuite.Key(e))).ToList();

            File.WriteAllBytes(file, bytes);
            var summary = new
            {
                suite = "stale",
                repo = repo.Name,
                commit = await repo.HeadAsync(),
                fuseBuild = await repo.VersionAsync(),
                target = Path.GetRelativePath(repo.Root, file).Replace('\\', '/'),
                postEditErrors = postEditErrors.Count,
                postEditMs,
                checkErrors = checkErrors.Count,
                checkMs = check.Milliseconds,
                stopErrors = stopErrors.Count,
                stopMs,
                stopBlocked = blocked,
                staleDelivered = stale.Count,
                staleLines = stale,
                rechecked = stale.Count == 0,
                treeCleanAfter = await repo.IsCleanAsync(),
            };
            Console.WriteLine($"[stale] post-edit {postEditErrors.Count} error(s); check after the fix {checkErrors.Count}; stop blocked: {blocked}, stale delivered: {stale.Count}");
            return summary;
        }
        finally
        {
            File.WriteAllBytes(file, bytes);
        }
    }

    /// <summary>Whether the post-edit probe would change <paramref name="file"/>, which needs a method with a body.</summary>
    private static bool Editable(string file)
    {
        var text = File.ReadAllText(file);
        return MultiAgentScenario.TextEdit(text, "1") != text;
    }

    private static async Task<(string Output, double Ms)> HookAsync(EvalRepo repo, string hookEvent, string payload)
    {
        var watch = Stopwatch.StartNew();
        var result = await repo.FuseHookAsync("claude", hookEvent, payload);
        return (result.Output, watch.Elapsed.TotalMilliseconds);
    }

    private static string PostEditPayload(EvalRepo repo, string file) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = "Edit",
            ["cwd"] = repo.Root,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = file },
        });

    private static string StopPayload(EvalRepo repo) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "Stop",
            ["cwd"] = repo.Root,
        });
}
