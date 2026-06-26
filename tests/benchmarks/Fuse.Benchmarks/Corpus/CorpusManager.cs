using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fuse.Benchmarks;

/// <summary>
///     Manages the pinned benchmark corpus: loads <c>corpus.json</c>, resolves and (optionally) clones
///     each repository at its pinned commit, loads the <c>prs.json</c> ground truth into an
///     <see cref="EvalDataset" />, and reconstructs pull-request change sets from merge history (the C#
///     port of <c>gen-prs.ps1</c>). It also provides git worktree helpers so a suite can materialize a
///     pull request's head tree without disturbing the pinned checkout.
/// </summary>
public sealed partial class CorpusManager
{
    private readonly string _benchRoot;
    private readonly string _corpusRoot;
    private readonly Action<string>? _log;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CorpusManager" /> class.
    /// </summary>
    /// <param name="benchRoot">The benchmark root holding <c>corpus.json</c> and <c>prs.json</c>.</param>
    /// <param name="corpusRoot">The directory holding (or to hold) the checked-out repositories.</param>
    /// <param name="log">An optional progress callback.</param>
    public CorpusManager(string benchRoot, string corpusRoot, Action<string>? log = null)
    {
        _benchRoot = benchRoot;
        _corpusRoot = corpusRoot;
        _log = log;
    }

    /// <summary>The resolved corpus root directory.</summary>
    public string CorpusRoot => _corpusRoot;

    /// <summary>
    ///     Loads and deserializes <c>corpus.json</c>.
    /// </summary>
    /// <returns>The corpus manifest.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <c>corpus.json</c> is missing.</exception>
    public CorpusManifest LoadManifest()
    {
        var path = Path.Combine(_benchRoot, "corpus.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"corpus.json not found at {path}.");
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), BenchmarkJsonContext.Default.CorpusManifest);
        return manifest ?? throw new InvalidOperationException("corpus.json deserialized to null.");
    }

    /// <summary>
    ///     Resolves the on-disk path of a corpus repository, whether it is an in-repo local fixture or a
    ///     cloned repository under the corpus root. Does not check existence.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <returns>The absolute path.</returns>
    public string ResolveRepoPath(CorpusRepo repo)
    {
        if (repo.Local is { } local)
        {
            // Local fixtures are repo-relative; benchRoot is tests/benchmarks, two levels under the repo root.
            var repoRoot = Path.GetFullPath(Path.Combine(_benchRoot, "..", ".."));
            return Path.GetFullPath(Path.Combine(repoRoot, local));
        }

        return Path.Combine(_corpusRoot, repo.Name);
    }

    /// <summary>
    ///     Reports whether a repository is present on disk (a directory with a <c>.git</c> entry, or a
    ///     local fixture directory).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <returns><see langword="true" /> when present.</returns>
    public bool IsPresent(CorpusRepo repo)
    {
        var path = ResolveRepoPath(repo);
        if (!Directory.Exists(path))
            return false;
        if (repo.Local is not null)
            return true;
        return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
    }

    /// <summary>
    ///     Ensures a repository is cloned and checked out at its pinned commit, cloning over the network
    ///     when absent. In-repo local fixtures and already-present clones are left untouched.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true" /> when the repository is available afterward.</returns>
    public async Task<bool> EnsureRepoAsync(CorpusRepo repo, CancellationToken cancellationToken)
    {
        if (IsPresent(repo))
            return true;
        if (repo.Url is null || repo.Commit is null)
            return false;

        var path = ResolveRepoPath(repo);
        Directory.CreateDirectory(_corpusRoot);
        _log?.Invoke($"corpus: cloning {repo.Name} from {repo.Url}");
        var clone = await GitCli.RunAsync(_corpusRoot, cancellationToken, "clone", "--no-checkout", repo.Url, path);
        if (!clone.Ok)
        {
            _log?.Invoke($"corpus: clone failed for {repo.Name}: {clone.StdErr.Trim()}");
            return false;
        }

        var checkout = await GitCli.RunAsync(path, cancellationToken, "checkout", "--detach", repo.Commit);
        if (!checkout.Ok)
        {
            _log?.Invoke($"corpus: checkout {repo.Commit} failed for {repo.Name}: {checkout.StdErr.Trim()}");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Loads <c>prs.json</c> into an <see cref="EvalDataset" />, resolving each repository's path and
    ///     lifting each PR record into a <see cref="PrTask" /> with its ground truth and signal bucket.
    /// </summary>
    /// <param name="datasetName">The dataset name to stamp on the result.</param>
    /// <returns>The dataset; repositories whose path cannot be resolved carry a null path.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <c>prs.json</c> is missing.</exception>
    public EvalDataset LoadDataset(string datasetName)
    {
        var prsPath = Path.Combine(_benchRoot, "prs.json");
        if (!File.Exists(prsPath))
            throw new FileNotFoundException($"prs.json not found at {prsPath}.");
        var records = JsonSerializer.Deserialize(File.ReadAllText(prsPath), BenchmarkJsonContext.Default.PrRecordArray)
                      ?? [];

        var manifest = LoadManifest();
        var repoByName = manifest.Repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var repos = records
            .GroupBy(r => r.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var path = repoByName.TryGetValue(group.Key, out var repo) && IsPresent(repo)
                    ? ResolveRepoPath(repo)
                    : null;
                var tasks = group.Select(ToTask).ToList();
                return new RepoTasks(group.Key, group.Key, path, tasks);
            })
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        return new EvalDataset(datasetName, repos);
    }

    private static PrTask ToTask(PrRecord record)
    {
        var files = record.ChangedCs
            .Select(p => new GroundTruthFile(Normalize(p), IsTestPath(p) ? "test" : "changed"))
            .ToList();
        return new PrTask(
            $"{record.Repo}#{record.Pr}", "pull_request", record.Repo, record.Pr,
            record.Base, record.Head, record.Merge, record.Title, Body: null,
            SignalBucket.Classify(record.Title),
            new GroundTruth(files, [], [], []));
    }

    private static bool IsTestPath(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/test", StringComparison.OrdinalIgnoreCase)
               || p.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith("Specs.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    ///     Reconstructs pull-request change sets from a repository's merge history (the C# port of
    ///     <c>gen-prs.ps1</c>): walk merge commits whose subject names a PR, take parent 1 as the base and
    ///     parent 2 as the head, keep only changes of 2 to 25 C# files, and drop misleading-maintenance
    ///     titles that cannot locate their own diff.
    /// </summary>
    /// <param name="repoPath">The repository path.</param>
    /// <param name="repoName">The repository name to stamp on each record.</param>
    /// <param name="perRepo">The maximum number of PRs to keep.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The reconstructed PR records, newest first.</returns>
    public async Task<IReadOnlyList<PrRecord>> ReconstructPullRequestsAsync(
        string repoPath,
        string repoName,
        int perRepo,
        CancellationToken cancellationToken)
    {
        var log = await GitCli.RunAsync(repoPath, cancellationToken,
            "log", "--merges", "--grep=Merge pull request", "--pretty=format:%H|%P|%s", "-n", "300");
        if (!log.Ok)
            return [];

        var picked = new List<PrRecord>();
        foreach (var line in log.Lines)
        {
            if (picked.Count >= perRepo)
                break;
            var parts = line.Split('|', 3);
            if (parts.Length < 3)
                continue;
            var merge = parts[0];
            var parents = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length < 2)
                continue;
            var baseRef = parents[0];
            var head = parents[1];
            var prMatch = PrNumber().Match(parts[2]);
            if (!prMatch.Success)
                continue;
            var pr = int.Parse(prMatch.Groups[1].Value);

            var diff = await GitCli.RunAsync(repoPath, cancellationToken,
                "diff", "--name-only", baseRef, head, "--", "*.cs");
            if (!diff.Ok)
                continue;
            var changed = diff.Lines
                .Where(f => !MachineGenerated().IsMatch(f))
                .ToList();
            if (changed.Count < 2 || changed.Count > 25)
                continue;

            var titleResult = await GitCli.RunAsync(repoPath, cancellationToken, "log", "-1", "--pretty=format:%s", head);
            var title = titleResult.StdOut.Trim();
            // Drop misleading-maintenance and empty titles (Test-PrTitleRelevant): a CI/dependency-bump title
            // over a real C# diff cannot locate its own change set and has no merge-noise fallback.
            if (string.IsNullOrWhiteSpace(title) || IsMaintenanceTitle(title))
                continue;

            picked.Add(new PrRecord(repoName, pr, merge, baseRef, head, title, changed));
        }

        return picked;
    }

    /// <summary>
    ///     Restores NuGet packages for a checkout so MSBuild and Roslyn can load it semantically. Without a
    ///     restore (a <c>project.assets.json</c>), <see cref="Fuse.Semantics.SemanticIndexer" /> falls back
    ///     to syntax-only indexing, capping every semantic suite. Bounded: a single solution target (or the
    ///     directory) is passed, never a variable-length project list.
    /// </summary>
    /// <param name="repoPath">The checkout or worktree root to restore.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     The restore outcome: whether at least one project restored, the restored and failed project
    ///     counts parsed from the restore output, and a short summary line.
    /// </returns>
    /// <remarks>
    ///     A partial restore is normal for a pinned OSS checkout on a newer SDK: some projects target a
    ///     framework or reference a package the current SDK cannot resolve and fail, while the rest restore
    ///     and can load semantically. The result reports both counts so the caller can decide.
    /// </remarks>
    public async Task<RestoreResult> RestoreAsync(string repoPath, CancellationToken cancellationToken)
    {
        var target = FindRestoreTarget(repoPath);
        var relativeTarget = target is null ? "(directory)" : Path.GetRelativePath(repoPath, target);
        _log?.Invoke($"corpus: restoring {Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar))} via {relativeTarget}");

        var result = target is null
            ? await DotnetCli.RunAsync(repoPath, cancellationToken, "restore")
            : await DotnetCli.RunAsync(repoPath, cancellationToken, "restore", target);

        // The restore output reports one "Restored <project>" or "Failed to restore <project>" line per
        // project; count both so a partial restore (some projects fail on a newer SDK) is visible.
        var restored = 0;
        var failed = 0;
        foreach (var line in result.Lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("Failed to restore", StringComparison.OrdinalIgnoreCase))
                failed++;
            else if (trimmed.StartsWith("Restored ", StringComparison.OrdinalIgnoreCase))
                restored++;
        }

        var ok = restored > 0 || (result.Ok && failed == 0);
        var summary = $"restored {restored}, failed {failed}";
        _log?.Invoke($"corpus: restore {relativeTarget}: {summary}");
        return new RestoreResult(ok, restored, failed, summary);
    }

    // Finds the best single solution to restore: prefer a solution whose file name matches the repo
    // directory name, then the shallowest path, then the first found. Returns null when no solution exists,
    // in which case the directory itself is restored (works when it holds a single project).
    private static string? FindRestoreTarget(string repoPath)
    {
        var solutions = Directory
            .EnumerateFiles(repoPath, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(repoPath, "*.slnx", SearchOption.AllDirectories))
            .Where(p => !IsUnderIgnoredDir(p))
            .ToList();
        if (solutions.Count == 0)
            return null;

        var repoName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar));
        return solutions
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Equals(repoName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => Path.GetFileNameWithoutExtension(p).Contains(repoName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static bool IsUnderIgnoredDir(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Adds a detached git worktree at a commit, returning its path. The caller must remove it with
    ///     <see cref="RemoveWorktreeAsync" />.
    /// </summary>
    /// <param name="repoPath">The repository path.</param>
    /// <param name="commit">The commit to check out.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The worktree path, or null when the worktree could not be created.</returns>
    public async Task<string?> AddWorktreeAsync(string repoPath, string commit, CancellationToken cancellationToken)
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), "fuse-bench-wt", Guid.NewGuid().ToString("N"));
        var result = await GitCli.RunAsync(repoPath, cancellationToken,
            "worktree", "add", "--detach", "--force", worktreePath, commit);
        return result.Ok ? worktreePath : null;
    }

    /// <summary>
    ///     Removes a git worktree previously created by <see cref="AddWorktreeAsync" />.
    /// </summary>
    /// <param name="repoPath">The repository path.</param>
    /// <param name="worktreePath">The worktree path.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the worktree is removed.</returns>
    public async Task RemoveWorktreeAsync(string repoPath, string worktreePath, CancellationToken cancellationToken)
    {
        await GitCli.RunAsync(repoPath, cancellationToken, "worktree", "remove", "--force", worktreePath);
        if (Directory.Exists(worktreePath))
        {
            try
            {
                Directory.Delete(worktreePath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }
    }

    private static bool IsMaintenanceTitle(string title)
    {
        // Mirrors the harness Test-PrTitleRelevant noise patterns: infra/dependency housekeeping titles.
        return MaintenanceNoise().IsMatch(title);
    }

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex PrNumber();

    [GeneratedRegex(@"(^|/)(bin|obj)/|\.g\.cs$|\.Designer\.cs$")]
    private static partial Regex MachineGenerated();

    [GeneratedRegex(@"^(ci|build|chore|deps|style|release)(\(|:)|^ci\b|^(bump|upgrade)\b|\bdependabot\b|\bfrom\s+v?\d[\w.\-]*\s+to\s+v?\d[\w.\-]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex MaintenanceNoise();
}
