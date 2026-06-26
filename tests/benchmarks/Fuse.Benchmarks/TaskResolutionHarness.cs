namespace Fuse.Benchmarks;

/// <summary>
///     The deterministic core of the task-resolution benchmark (R9): per task, materialize an isolated git
///     worktree at the base commit, apply a candidate patch, run the repository's test oracle, and report
///     whether the patch applied and whether the tests passed. The agent that produces the patch is a
///     pluggable, compute-heavy layer on top; this core is what scores patch pass@1 and is exercised
///     deterministically by a trivial fixture so the oracle itself is trusted.
/// </summary>
/// <remarks>
///     Each task runs in its own worktree, so a failed or destructive patch cannot corrupt the next task: the
///     worktree is removed in a finally block whatever the outcome. The test oracle is an external command
///     (for example <c>dotnet test</c>); its exit code is the pass/fail signal.
/// </remarks>
public sealed class TaskResolutionHarness
{
    private readonly CorpusManager _manager;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TaskResolutionHarness" /> class.
    /// </summary>
    /// <param name="manager">The corpus manager providing worktree isolation.</param>
    public TaskResolutionHarness(CorpusManager manager) => _manager = manager;

    /// <summary>
    ///     Resolves one task: check out <paramref name="baseRef" /> in an isolated worktree, apply the patch,
    ///     run the oracle, and report the outcome. The worktree is always removed.
    /// </summary>
    /// <param name="repoPath">The repository path.</param>
    /// <param name="baseRef">The base commit to check out.</param>
    /// <param name="patch">The unified-diff patch to apply, or null or empty to score the base as-is.</param>
    /// <param name="oracle">The test oracle command and arguments run in the worktree.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolution outcome.</returns>
    public async Task<TaskResolutionResult> ResolveAsync(
        string repoPath,
        string baseRef,
        string? patch,
        OracleCommand oracle,
        CancellationToken cancellationToken)
    {
        var worktree = await _manager.AddWorktreeAsync(repoPath, baseRef, cancellationToken);
        if (worktree is null)
            return new TaskResolutionResult(false, false, "worktree creation failed");

        try
        {
            var applied = true;
            if (!string.IsNullOrWhiteSpace(patch))
                applied = await ApplyPatchAsync(worktree, patch!, cancellationToken);

            if (!applied)
                return new TaskResolutionResult(false, false, "patch did not apply");

            var oracleResult = await RunOracleAsync(worktree, oracle, cancellationToken);
            return new TaskResolutionResult(true, oracleResult.Ok, oracleResult.Ok ? "tests passed" : "tests failed");
        }
        finally
        {
            await _manager.RemoveWorktreeAsync(repoPath, worktree, cancellationToken);
        }
    }

    // Applies a unified-diff patch via "git apply", reading the patch from stdin so the argument list stays
    // bounded regardless of patch size.
    private static async Task<bool> ApplyPatchAsync(string worktree, string patch, CancellationToken cancellationToken)
    {
        var result = await GitCli.RunWithStdinAsync(worktree, patch, cancellationToken, "apply", "--whitespace=nowarn", "-");
        return result.Ok;
    }

    private static async Task<GitCli.GitResult> RunOracleAsync(string worktree, OracleCommand oracle, CancellationToken cancellationToken)
    {
        if (string.Equals(oracle.FileName, "git", StringComparison.OrdinalIgnoreCase))
            return await GitCli.RunAsync(worktree, cancellationToken, oracle.Arguments.ToArray());
        return await ProcessRunner.RunAsync(oracle.FileName, worktree, cancellationToken, oracle.Arguments.ToArray());
    }
}

/// <summary>
///     A test oracle: the external command run in the worktree to decide whether a patch resolves the task.
/// </summary>
/// <param name="FileName">The executable (for example <c>dotnet</c> or <c>git</c>).</param>
/// <param name="Arguments">The bounded, fixed arguments.</param>
public sealed record OracleCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
///     The outcome of resolving one task.
/// </summary>
/// <param name="PatchApplied">Whether the candidate patch applied cleanly.</param>
/// <param name="TestsPassed">Whether the test oracle passed after the patch.</param>
/// <param name="Detail">A short human-readable detail.</param>
public sealed record TaskResolutionResult(bool PatchApplied, bool TestsPassed, string Detail);
