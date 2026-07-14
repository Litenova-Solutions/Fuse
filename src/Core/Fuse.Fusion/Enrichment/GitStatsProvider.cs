using System.Diagnostics;
using System.Globalization;
using System.Text;
using Fuse.Emission.Models;
using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Enrichment;

/// <summary>
///     Collects per-file git churn and last-modified data via git subprocess calls.
/// </summary>
/// <remarks>
///     Issues two batched <c>git log</c> passes for the path set (one within the lookback window for churn counts,
///     one for all-time last-modified dates) instead of per-file subprocess fan-out. Paths are chunked so the
///     quoted pathspec list stays under the OS command-line limit. Enrichment is best-effort: a missing git
///     executable, a non-repository directory, or any failing git command yields an unavailable result or zeroed
///     values rather than throwing.
/// </remarks>
/// <seealso cref="IGitStatsProvider" />
public sealed class GitStatsProvider : IGitStatsProvider
{
    // Leave headroom under the Windows CreateProcess limit (~8191 on older builds, ~32 KB on recent ones).
    private const int MaxPathArgumentChars = 7000;

    /// <summary>
    ///     Default lookback window for commit churn counts.
    /// </summary>
    public static readonly TimeSpan DefaultLookback = TimeSpan.FromDays(90);

    /// <inheritdoc />
    public async Task<GitStatsResult> GetStatsAsync(
        string sourceDirectory,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        if (relativePaths.Count == 0)
            return new GitStatsResult(false, new Dictionary<string, GitFileStats>());

        var gitPath = GitExecutableLocator.Find();
        if (gitPath is null)
            return Unavailable();

        if (!await IsInsideWorkTreeAsync(gitPath, sourceDirectory, cancellationToken))
            return Unavailable();

        var pathBatches = BuildPathArgumentBatches(relativePaths);
        var sinceArg = $"--since=\"{DefaultLookback.TotalDays:F0} days ago\"";
        var pathLookup = BuildPathLookup(relativePaths);

        var commitCounts = InitializeCounts(relativePaths);
        var lastModified = InitializeLastModified(relativePaths);

        var churnLogTask = RunBatchedGitLogAsync(
            gitPath,
            sourceDirectory,
            sinceArg,
            pathBatches,
            cancellationToken);

        var lastModifiedLogTask = RunBatchedGitLogAsync(
            gitPath,
            sourceDirectory,
            sinceArg: null,
            pathBatches,
            cancellationToken);

        await Task.WhenAll(churnLogTask, lastModifiedLogTask);

        ApplyCommitCountsFromLog(await churnLogTask, pathLookup, commitCounts);
        ApplyLastModifiedFromLog(await lastModifiedLogTask, pathLookup, lastModified);

        var stats = new Dictionary<string, GitFileStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stats[relativePath] = new GitFileStats(
                relativePath,
                commitCounts[relativePath],
                lastModified[relativePath]);
        }

        return new GitStatsResult(true, stats);
    }

    private static GitStatsResult Unavailable() =>
        new(false, new Dictionary<string, GitFileStats>());

    private static Dictionary<string, int> InitializeCounts(IReadOnlyList<string> relativePaths)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
            counts.TryAdd(path, 0);

        return counts;
    }

    private static Dictionary<string, DateTimeOffset?> InitializeLastModified(IReadOnlyList<string> relativePaths)
    {
        var lastModified = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
            lastModified.TryAdd(path, null);

        return lastModified;
    }

    private static Dictionary<string, string> BuildPathLookup(IReadOnlyList<string> relativePaths)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
            lookup.TryAdd(NormalizeGitPath(path), path);

        return lookup;
    }

    private static IReadOnlyList<string> BuildPathArgumentBatches(IReadOnlyList<string> relativePaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<string>();
        var current = new StringBuilder();

        foreach (var path in relativePaths)
        {
            if (!seen.Add(path))
                continue;

            var quoted = QuoteGitPath(path);
            var addition = current.Length == 0 ? quoted : " " + quoted;
            if (current.Length > 0 && current.Length + addition.Length > MaxPathArgumentChars)
            {
                batches.Add(current.ToString());
                current.Clear();
                addition = quoted;
            }

            current.Append(addition);
        }

        if (current.Length > 0)
            batches.Add(current.ToString());

        return batches;
    }

    private static async Task<string> RunBatchedGitLogAsync(
        string gitPath,
        string workingDirectory,
        string? sinceArg,
        IReadOnlyList<string> pathBatches,
        CancellationToken cancellationToken)
    {
        if (pathBatches.Count == 0)
            return string.Empty;

        var combined = new StringBuilder();
        foreach (var pathArguments in pathBatches)
        {
            var arguments = new StringBuilder("-c core.quotepath=false log ");
            if (!string.IsNullOrEmpty(sinceArg))
                arguments.Append(sinceArg).Append(' ');

            arguments.Append("--name-only --format=%cI HEAD -- ").Append(pathArguments);

            var result = await RunGitAsync(gitPath, workingDirectory, arguments.ToString(), cancellationToken);
            if (result.ExitCode != 0)
                continue;

            combined.Append(result.Stdout);
        }

        return combined.ToString();
    }

    private static void ApplyCommitCountsFromLog(
        string stdout,
        IReadOnlyDictionary<string, string> pathLookup,
        Dictionary<string, int> commitCounts)
    {
        foreach (var line in EnumerateLogLines(stdout))
        {
            if (IsCommitDateLine(line))
                continue;

            if (!pathLookup.TryGetValue(NormalizeGitPath(line), out var key))
                continue;

            commitCounts[key]++;
        }
    }

    private static void ApplyLastModifiedFromLog(
        string stdout,
        IReadOnlyDictionary<string, string> pathLookup,
        Dictionary<string, DateTimeOffset?> lastModified)
    {
        DateTimeOffset? currentCommitDate = null;

        foreach (var line in EnumerateLogLines(stdout))
        {
            if (TryParseCommitDate(line, out var commitDate))
            {
                currentCommitDate = commitDate;
                continue;
            }

            if (!pathLookup.TryGetValue(NormalizeGitPath(line), out var key))
                continue;

            if (lastModified[key] is not null || currentCommitDate is null)
                continue;

            lastModified[key] = currentCommitDate;
        }
    }

    private static IEnumerable<string> EnumerateLogLines(string stdout)
    {
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            yield return line;
        }
    }

    // Commit dates are emitted via --format=%cI; path lines follow. A path that parses as ISO-8601 (for example
    // a directory named 2024-01-01) would be misclassified; that edge case is negligible for typical repos.
    private static bool IsCommitDateLine(string line) => TryParseCommitDate(line, out _);

    private static bool TryParseCommitDate(string line, out DateTimeOffset commitDate) =>
        DateTimeOffset.TryParse(
            line,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out commitDate);

    private static string NormalizeGitPath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal);

    private static async Task<bool> IsInsideWorkTreeAsync(
        string gitPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            gitPath,
            workingDirectory,
            "rev-parse --is-inside-work-tree",
            cancellationToken);

        return result.ExitCode == 0 &&
               result.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string gitPath,
        string workingDirectory,
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = gitPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                return (-1, string.Empty, "Failed to start git process.");
        }
        catch
        {
            return (-1, string.Empty, "Failed to start git process.");
        }

        // Close git's stdin so it gets EOF, never the parent's inherited stdin (the live MCP client pipe in
        // `fuse mcp serve`); git never reads stdin for these commands.
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string QuoteGitPath(string relativePath) =>
        $"\"{relativePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
