using System.Diagnostics;
using System.Globalization;
using Fuse.Emission.Models;

namespace Fuse.Fusion.Enrichment;

/// <summary>
///     Collects per-file git churn and last-modified data via git subprocess calls.
/// </summary>
/// <remarks>
///     Issues two batched <c>git log</c> calls for the full path set (one within the lookback window for churn
///     counts, one for all-time last-modified dates) instead of per-file subprocess fan-out. Enrichment is
///     best-effort: a missing git executable, a non-repository directory, or any failing git command yields an
///     unavailable result or zeroed values rather than throwing.
/// </remarks>
/// <seealso cref="IGitStatsProvider" />
public sealed class GitStatsProvider : IGitStatsProvider
{
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

        var gitPath = FindGitExecutable();
        if (gitPath is null)
            return Unavailable();

        if (!await IsInsideWorkTreeAsync(gitPath, sourceDirectory, cancellationToken))
            return Unavailable();

        var pathArguments = BuildPathArguments(relativePaths);
        var sinceArg = $"--since=\"{DefaultLookback.TotalDays:F0} days ago\"";
        var pathLookup = BuildPathLookup(relativePaths);

        var commitCounts = InitializeCounts(relativePaths);
        var lastModified = InitializeLastModified(relativePaths);

        var churnLogTask = RunGitAsync(
            gitPath,
            sourceDirectory,
            $"log {sinceArg} --name-only --format=%cI HEAD -- {pathArguments}",
            cancellationToken);

        var lastModifiedLogTask = RunGitAsync(
            gitPath,
            sourceDirectory,
            $"log --name-only --format=%cI HEAD -- {pathArguments}",
            cancellationToken);

        await Task.WhenAll(churnLogTask, lastModifiedLogTask);

        var churnLog = await churnLogTask;
        if (churnLog.ExitCode == 0)
            ApplyCommitCountsFromLog(churnLog.Stdout, pathLookup, commitCounts);

        var lastModifiedLog = await lastModifiedLogTask;
        if (lastModifiedLog.ExitCode == 0)
            ApplyLastModifiedFromLog(lastModifiedLog.Stdout, pathLookup, lastModified);

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

    private static string BuildPathArguments(IReadOnlyList<string> relativePaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var quoted = new List<string>(relativePaths.Count);

        foreach (var path in relativePaths)
        {
            if (seen.Add(path))
                quoted.Add(QuoteGitPath(path));
        }

        return string.Join(' ', quoted);
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string QuoteGitPath(string relativePath) =>
        $"\"{relativePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string? FindGitExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
