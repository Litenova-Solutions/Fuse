using System.Diagnostics;

namespace Fuse.Analysis.Git;

/// <summary>
///     Collects per-file git churn and last-modified data via git subprocess calls.
/// </summary>
/// <remarks>
///     Runs one <c>git rev-list</c> and one <c>git log</c> per file, so cost scales with the number of
///     paths. Enrichment is best-effort: a missing git executable, a non-repository directory, or any
///     failing git command yields an unavailable result or zeroed values rather than throwing.
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

        var sinceArg = $"--since=\"{DefaultLookback.TotalDays:F0} days ago\"";
        var stats = new Dictionary<string, GitFileStats>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commitCount = await GetCommitCountAsync(
                gitPath,
                sourceDirectory,
                sinceArg,
                relativePath,
                cancellationToken);

            var lastModified = await GetLastModifiedAsync(
                gitPath,
                sourceDirectory,
                relativePath,
                cancellationToken);

            stats[relativePath] = new GitFileStats(relativePath, commitCount, lastModified);
        }

        return new GitStatsResult(true, stats);
    }

    private static GitStatsResult Unavailable() =>
        new(false, new Dictionary<string, GitFileStats>());

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

    private static async Task<int> GetCommitCountAsync(
        string gitPath,
        string workingDirectory,
        string sinceArg,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var quotedPath = QuoteGitPath(relativePath);
        var result = await RunGitAsync(
            gitPath,
            workingDirectory,
            $"rev-list --count {sinceArg} HEAD -- {quotedPath}",
            cancellationToken);

        if (result.ExitCode != 0)
            return 0;

        return int.TryParse(result.Stdout.Trim(), out var count) ? count : 0;
    }

    private static async Task<DateTimeOffset?> GetLastModifiedAsync(
        string gitPath,
        string workingDirectory,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var quotedPath = QuoteGitPath(relativePath);
        var result = await RunGitAsync(
            gitPath,
            workingDirectory,
            $"log -1 --format=%cI -- {quotedPath}",
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return null;

        return DateTimeOffset.TryParse(result.Stdout.Trim(), out var parsed) ? parsed : null;
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
