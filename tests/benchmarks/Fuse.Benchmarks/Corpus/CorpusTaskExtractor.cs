using System.Text.RegularExpressions;

namespace Fuse.Benchmarks;

/// <summary>
///     A candidate fail-to-pass oracle task mined from a repository's history (C4): a commit whose diff over its
///     first parent changes both test files and non-test source, so the changed tests plausibly exercise the
///     changed behavior. The task is a usable oracle only once <see cref="TaskOracle" /> confirms the changed
///     tests fail on the base (with the tests applied) and pass on the merge; extraction produces the candidate,
///     verification decides.
/// </summary>
/// <param name="Repo">The repository name.</param>
/// <param name="Commit">The merge commit (the state where the tests pass).</param>
/// <param name="BaseCommit">The first-parent base commit (the state before the change).</param>
/// <param name="Title">The commit subject.</param>
/// <param name="TestFiles">The changed test files (repo-relative).</param>
/// <param name="TestFilter">The derived <c>dotnet test --filter</c> expression selecting the changed tests.</param>
public sealed record CandidateTask(
    string Repo,
    string Commit,
    string BaseCommit,
    string Title,
    IReadOnlyList<string> TestFiles,
    string TestFilter);

/// <summary>
///     The outcome of extracting and verifying one candidate task (C4).
/// </summary>
/// <param name="Candidate">The candidate.</param>
/// <param name="Verified">Whether the oracle confirmed fail-to-pass and not flaky.</param>
/// <param name="Reason">The verdict reason (or the skip reason when not verified).</param>
public sealed record TaskVerification(CandidateTask Candidate, bool Verified, string Reason);

/// <summary>
///     Mines candidate fail-to-pass oracle tasks from a corpus repository's git history and verifies them with
///     <see cref="TaskOracle" /> (C4). A candidate is a commit that changed both a test file and non-test source;
///     verification materializes the base commit with only the commit's test changes applied (so the new tests run
///     against the old code) and the merge commit as-is, runs the changed tests at both, and keeps the task only
///     when it fails on base and passes on merge without flaking. This turns a real merged change into a task a
///     model-driven suite can score green against, with the oracle proven mechanically rather than assumed.
/// </summary>
public sealed class CorpusTaskExtractor
{
    private readonly CorpusManager _manager;
    private readonly Func<string, string, CancellationToken, Task<TestRunOutcome>> _runTests;

    /// <summary>Initializes a new instance of the <see cref="CorpusTaskExtractor" /> class.</summary>
    /// <param name="manager">The corpus manager (worktree isolation).</param>
    /// <param name="runTests">
    ///     The test runner used by the oracle (worktree, filter) -&gt; outcome; defaults to
    ///     <see cref="TaskOracle.RunDotnetTestAsync" /> when null. Injected so the pipeline can be tested offline.
    /// </param>
    public CorpusTaskExtractor(
        CorpusManager manager,
        Func<string, string, CancellationToken, Task<TestRunOutcome>>? runTests = null)
    {
        _manager = manager;
        _runTests = runTests ?? TaskOracle.RunDotnetTestAsync;
    }

    /// <summary>
    ///     Mines up to <paramref name="maxCandidates" /> candidate tasks from the most recent
    ///     <paramref name="scanCommits" /> first-parent commits reachable from the repository's checked-out head.
    /// </summary>
    /// <param name="repoPath">The repository path (checked out at its pinned commit).</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="scanCommits">How many first-parent commits to scan back from head.</param>
    /// <param name="maxCandidates">The maximum candidates to return.</param>
    /// <param name="cancellationToken">A token to cancel the mine.</param>
    /// <returns>The candidate tasks, most recent first.</returns>
    public async Task<IReadOnlyList<CandidateTask>> MineCandidatesAsync(
        string repoPath, string repoName, int scanCommits, int maxCandidates, CancellationToken cancellationToken)
    {
        // Walk the mainline (first parent) INCLUDING merge commits: a merge-workflow repo lands each PR as a merge
        // commit, so --no-merges would strip almost the whole history (leaving only the rare direct push). Each
        // commit's PR content is its first-parent diff (computed per candidate below), which works for both a
        // squash-merge repo (single-parent commits) and a merge-commit repo (the merge's first-parent diff).
        var log = await GitCli.RunAsync(repoPath, cancellationToken,
            "log", "--first-parent", $"-n{scanCommits}", "--format=%H %s");
        if (!log.Ok)
            return [];

        var candidates = new List<CandidateTask>();
        foreach (var line in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidates.Count >= maxCandidates)
                break;
            cancellationToken.ThrowIfCancellationRequested();

            // Each line is "<full-sha> <subject>"; a commit hash never contains a space, so split on the first.
            var sep = line.IndexOf(' ');
            var commit = (sep < 0 ? line : line[..sep]).Trim();
            var title = sep < 0 ? "" : line[(sep + 1)..].Trim();
            if (commit.Length == 0)
                continue;

            // First-parent diff (commit^ .. commit): the PR's full file set. Unlike `diff-tree <merge>`, which
            // shows nothing for a merge commit (git diffs against all parents), this yields the merged change.
            var names = await GitCli.RunAsync(repoPath, cancellationToken,
                "diff", "--name-only", $"{commit}^", commit);
            if (!names.Ok)
                continue;

            var changed = names.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var (testFiles, sourceFiles) = Classify(changed);
            if (testFiles.Count == 0 || sourceFiles.Count == 0)
                continue; // Need both a behavior change and a test change to be a fail-to-pass candidate.

            var filter = await DeriveTestFilterAsync(repoPath, commit, testFiles, cancellationToken);
            if (filter is null)
                continue; // No recognizable test class in the changed test files.

            var baseParent = await GitCli.RunAsync(repoPath, cancellationToken, "rev-parse", $"{commit}^");
            if (!baseParent.Ok)
                continue;

            candidates.Add(new CandidateTask(repoName, commit, baseParent.StdOut.Trim(), title, testFiles, filter));
        }

        return candidates;
    }

    /// <summary>
    ///     Verifies a candidate: materializes the base commit with only the candidate's test changes applied and
    ///     the merge commit as-is, then runs the changed tests at both through <see cref="TaskOracle" />.
    /// </summary>
    /// <param name="repoPath">The repository path.</param>
    /// <param name="candidate">The candidate to verify.</param>
    /// <param name="cancellationToken">A token to cancel the verification.</param>
    /// <returns>The verification outcome (verified, or the concrete skip/verdict reason).</returns>
    public async Task<TaskVerification> VerifyAsync(
        string repoPath, CandidateTask candidate, CancellationToken cancellationToken)
    {
        string? baseWt = null;
        string? mergeWt = null;
        try
        {
            baseWt = await _manager.AddWorktreeAsync(repoPath, candidate.BaseCommit, cancellationToken);
            mergeWt = await _manager.AddWorktreeAsync(repoPath, candidate.Commit, cancellationToken);
            if (baseWt is null || mergeWt is null)
                return new TaskVerification(candidate, false, "worktree creation failed");

            // Apply only the test-file changes onto the base worktree, so the new tests run against the old code.
            var testDiff = await GitCli.RunAsync(repoPath, cancellationToken,
                ["diff", candidate.BaseCommit, candidate.Commit, "--", .. candidate.TestFiles]);
            if (!testDiff.Ok)
                return new TaskVerification(candidate, false, "could not compute the test-only diff");
            if (!string.IsNullOrWhiteSpace(testDiff.StdOut))
            {
                var apply = await GitCli.RunWithStdinAsync(baseWt, testDiff.StdOut, cancellationToken,
                    "apply", "--whitespace=nowarn", "-");
                if (!apply.Ok)
                    return new TaskVerification(candidate, false, "the test-only diff did not apply onto the base");
            }

            var oracle = new TaskOracle(_runTests);
            var verdict = await oracle.VerifyAsync(baseWt, mergeWt, candidate.TestFilter, cancellationToken);
            return new TaskVerification(candidate, verdict.Verified, verdict.Reason);
        }
        finally
        {
            if (baseWt is not null)
                await _manager.RemoveWorktreeAsync(repoPath, baseWt, cancellationToken);
            if (mergeWt is not null)
                await _manager.RemoveWorktreeAsync(repoPath, mergeWt, cancellationToken);
        }
    }

    // Splits changed C# files into test files and non-test source files by path heuristic.
    internal static (List<string> TestFiles, List<string> SourceFiles) Classify(IEnumerable<string> changedCsFiles)
    {
        var tests = new List<string>();
        var sources = new List<string>();
        foreach (var file in changedCsFiles)
        {
            if (IsTestFile(file))
                tests.Add(file);
            else
                sources.Add(file);
        }

        return (tests, sources);
    }

    // A path is a test file when a segment or file name signals a test project or test file (mirrors the
    // corpus-health discovery heuristic so the two agree on what counts as a test).
    internal static bool IsTestFile(string path)
    {
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        if (!lower.EndsWith(".cs"))
            return false;
        return lower.Contains("/test") || lower.Contains(".test") || lower.Contains("tests/")
               || lower.EndsWith("tests.cs") || lower.EndsWith("test.cs") || lower.EndsWith("specs.cs") || lower.EndsWith("spec.cs");
    }

    // Derives a dotnet-test --filter from the test classes declared in the changed test files at the commit. The
    // filter unions each class by FullyQualifiedName~ClassName, which selects that class's tests regardless of
    // namespace. Returns null when no test class is found (nothing safe to filter on).
    private async Task<string?> DeriveTestFilterAsync(
        string repoPath, string commit, IReadOnlyList<string> testFiles, CancellationToken cancellationToken)
    {
        var classNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in testFiles)
        {
            var show = await GitCli.RunAsync(repoPath, cancellationToken, "show", $"{commit}:{file}");
            if (!show.Ok)
                continue;
            foreach (var name in ExtractTestClassNames(show.StdOut))
                classNames.Add(name);
        }

        return BuildFilter(classNames);
    }

    // Extracts likely test class names from C# source: a class whose name signals a test (ends with Tests/Test/
    // Specs/Spec/Fixture) or whose body carries a test attribute. Pure and unit-tested.
    internal static IEnumerable<string> ExtractTestClassNames(string source)
    {
        var names = new List<string>();
        var hasTestAttribute = Regex.IsMatch(source, @"\[\s*(Fact|Theory|Test|TestMethod|TestCase)\b");
        foreach (Match m in Regex.Matches(source, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)"))
        {
            var name = m.Groups[1].Value;
            var looksLikeTest = name.EndsWith("Tests", StringComparison.Ordinal)
                                || name.EndsWith("Test", StringComparison.Ordinal)
                                || name.EndsWith("Specs", StringComparison.Ordinal)
                                || name.EndsWith("Spec", StringComparison.Ordinal)
                                || name.EndsWith("Fixture", StringComparison.Ordinal);
            if (looksLikeTest || hasTestAttribute)
                names.Add(name);
        }

        return names;
    }

    // Builds the union filter expression from the class names, or null when there are none.
    internal static string? BuildFilter(IReadOnlyCollection<string> classNames)
    {
        if (classNames.Count == 0)
            return null;
        return string.Join("|", classNames.Select(n => $"FullyQualifiedName~{n}"));
    }
}
