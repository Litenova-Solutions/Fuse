using System.Diagnostics;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Mines a bounded window of git history for file co-change couplings (files that change together in the
///     same commit) and persists them so the open-ended scorer can recover the sibling files of a multi-file
///     change. Best-effort: a missing git executable, a non-repository directory, or any failing git command
///     yields no rows rather than throwing, so indexing never depends on git.
/// </summary>
/// <remarks>
///     The single <c>git log</c> invocation has a fixed argument list (a commit cap, no variable path list), so
///     the external-process command line is bounded by construction, honoring the bounded-args invariant. The
///     pairwise blow-up is bounded two ways: wide commits (more than <see cref="MaxFilesPerCommit" /> source
///     files, typically merges or sweeps) are skipped, and only pairs seen at least <see cref="MinPairCount" />
///     times are emitted. The window is capped at <see cref="MaxCommits" /> commits, so the mining cost added to
///     the cold index is bounded.
/// </remarks>
public sealed class GitCoChangeCollector
{
    /// <summary>The environment variable that enables git co-change collection at index time (R41).</summary>
    public const string EnvVar = "FUSE_COCHANGE";

    /// <summary>
    ///     Whether git co-change collection runs on the index path (R41). Default off: the git co-change prior is
    ///     off in the shipping ranking (Decision D6, discharged as net-negative on the corpus), so collecting and
    ///     storing co-change on every index is wasted work - the <c>git log</c> walk was a large share of the
    ///     index hot path in <c>profile-v42.json</c>. Gated behind the same signal as the prior; the ranking
    ///     diagnostic that enables the prior sets <c>FUSE_COCHANGE=1</c> to collect the data it measures.
    /// </summary>
    /// <returns><see langword="true" /> only when explicitly enabled.</returns>
    public static bool IsCollectionEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        return value is not null
               && (value.Equals("1", StringComparison.Ordinal)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The maximum number of recent commits mined, bounding the cold-index cost.</summary>
    public const int MaxCommits = 1000;

    /// <summary>A commit touching more source files than this is skipped as a wide sweep, bounding the pair blow-up.</summary>
    public const int MaxFilesPerCommit = 40;

    /// <summary>A pair must co-change at least this many times to be persisted, dropping one-off noise.</summary>
    public const int MinPairCount = 2;

    // A hard ceiling on each git call so a stuck git subprocess (a pager, a credential prompt, a lock) can never
    // hang indexing: the process is killed and the call degrades to no co-change data.
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    // Marks the start of a commit in the git log output (a control char that cannot appear in a path), followed
    // by the commit's ISO-8601 date; subsequent non-empty lines are the commit's changed file paths.
    private const char CommitMarker = '\x01';

    // The source-code extensions whose co-change is meaningful for retrieval. Bounded so config, lock, and asset
    // churn does not flood the pair table; language-agnostic by listing the common code families.
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".ts", ".tsx", ".js", ".jsx", ".fs", ".vb", ".go", ".rs", ".java", ".kt", ".rb", ".cpp", ".cc", ".c", ".h", ".hpp"
    };

    /// <summary>
    ///     Mines the co-change couplings of a repository and writes them to the store, replacing any prior set.
    ///     A no-op (clears nothing) when git is unavailable or the directory is not a work tree.
    /// </summary>
    /// <param name="rootDirectory">The repository root.</param>
    /// <param name="store">The index store to write the co-change table to.</param>
    /// <param name="cancellationToken">A token to cancel mining.</param>
    /// <returns>The number of co-change pairs persisted.</returns>
    public async Task<int> CollectAndStoreAsync(string rootDirectory, IWorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        var records = await CollectAsync(rootDirectory, cancellationToken);
        if (records.Count == 0)
            return 0;

        await store.UpsertCoChangesAsync(records, cancellationToken);
        return records.Count;
    }

    /// <summary>
    ///     Mines the co-change couplings of a repository without writing them, returning the computed pairs.
    /// </summary>
    /// <param name="rootDirectory">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel mining.</param>
    /// <returns>The co-change pairs, ordered deterministically; empty when git is unavailable.</returns>
    public async Task<IReadOnlyList<CoChangeRecord>> CollectAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!await IsWorkTreeAsync(root, cancellationToken))
            return [];

        var log = await RunGitLogAsync(root, cancellationToken);
        if (log is null)
            return [];

        return BuildRecords(ParseCommits(log));
    }

    /// <summary>
    ///     Builds the co-change records from raw <c>git log</c> output (a <c>\x01</c>-prefixed ISO date per
    ///     commit, followed by its changed file paths), applying the wide-commit and minimum-pair-count bounds.
    ///     Exposed so the aggregation can be tested deterministically without invoking git.
    /// </summary>
    /// <param name="log">The git log text in the collector's mining format.</param>
    /// <returns>The co-change pairs, ordered by coupling strength.</returns>
    public IReadOnlyList<CoChangeRecord> ParseLog(string log) => BuildRecords(ParseCommits(log));

    // Parses the git log output into per-commit (date, source file set), skipping empty and wide commits.
    private static List<(string Date, List<string> Files)> ParseCommits(string log)
    {
        var commits = new List<(string Date, List<string> Files)>();
        string? date = null;
        var files = new List<string>();

        void Flush()
        {
            if (date is not null && files.Count is > 0 and <= MaxFilesPerCommit)
                commits.Add((date, [.. files]));
        }

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (line[0] == CommitMarker)
            {
                Flush();
                date = line[1..];
                files.Clear();
                continue;
            }

            if (SourceExtensions.Contains(Path.GetExtension(line)))
                files.Add(line.Replace('\\', '/'));
        }

        Flush();
        return commits;
    }

    // Counts per-file and per-pair commit occurrences, then computes PMI and Jaccard for pairs above the floor.
    private static List<CoChangeRecord> BuildRecords(List<(string Date, List<string> Files)> commits)
    {
        var total = commits.Count;
        if (total == 0)
            return [];

        var fileCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairCount = new Dictionary<(string A, string B), int>();
        var pairLastSeen = new Dictionary<(string A, string B), string>();

        foreach (var (date, files) in commits)
        {
            var distinct = files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
            foreach (var f in distinct)
                fileCount[f] = fileCount.GetValueOrDefault(f) + 1;

            for (var i = 0; i < distinct.Count; i++)
            {
                for (var j = i + 1; j < distinct.Count; j++)
                {
                    var key = (distinct[i], distinct[j]);
                    pairCount[key] = pairCount.GetValueOrDefault(key) + 1;
                    // Commits are newest-first from git log, so the first date seen for a pair is the most recent.
                    pairLastSeen.TryAdd(key, date);
                }
            }
        }

        var records = new List<CoChangeRecord>();
        foreach (var (key, count) in pairCount)
        {
            if (count < MinPairCount)
                continue;

            var ca = fileCount[key.A];
            var cb = fileCount[key.B];
            // PMI in log2: how much more often the pair co-occurs than chance; Jaccard: overlap of the two files'
            // commit sets. Both are guarded against division by zero (count >= MinPairCount >= 1 implies ca, cb > 0).
            var pmi = Math.Log2((double)count * total / (ca * (double)cb));
            var jaccard = (double)count / (ca + cb - count);
            records.Add(new CoChangeRecord(key.A, key.B, count, pmi, jaccard, pairLastSeen.GetValueOrDefault(key)));
        }

        // Deterministic order: strongest coupling first, then by path, so a capped consumer sees a stable set.
        return records
            .OrderByDescending(r => r.Jaccard)
            .ThenBy(r => r.PathA, StringComparer.Ordinal)
            .ThenBy(r => r.PathB, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<bool> IsWorkTreeAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(root, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        return result is { ExitCode: 0 } && result.Value.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> RunGitLogAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            root,
            ["-c", "core.quotepath=false", "log", "--no-merges", "--name-only", $"--format={CommitMarker}%cI", "-n", MaxCommits.ToString(), "HEAD"],
            cancellationToken);
        return result is { ExitCode: 0 } ? result.Value.Stdout : null;
    }

    // Runs git with an argument list (each argument passed separately, so no shell quoting and no command-line
    // concatenation), capturing stdout. Returns null on any failure so the caller degrades to no co-change data.
    private static async Task<(int ExitCode, string Stdout)?> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // Never let git block on a pager or an interactive credential/prompt: those would hang the index.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";

        // A hard timeout on top of the caller's token so a stuck git is killed rather than hanging indexing.
        using var timeout = new CancellationTokenSource(GitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            // Close git's stdin so it gets EOF, never the parent's inherited stdin (the live MCP client pipe in
            // `fuse mcp serve`); git never reads stdin for these commands.
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            _ = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            return (process.ExitCode, await stdoutTask);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The git call exceeded its timeout: kill it and degrade to no co-change data.
            TryKill(process);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may have already exited.
        }
    }
}
