using System.Diagnostics;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Detects changed files by invoking <c>git diff --name-only</c> as a subprocess.
/// </summary>
/// <remarks>
///     Resolves the git executable from <c>PATH</c> and runs in the source directory; the result
///     reflects whatever git reports for the supplied ref, so accuracy depends on the repository state.
/// </remarks>
/// <seealso cref="IChangeDetector" />
public sealed class GitChangeDetector : IChangeDetector
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
        string sourceDirectory,
        string since,
        CancellationToken cancellationToken = default)
    {
        var gitPath = FindGitExecutable();
        if (gitPath is null)
            throw new ChangeDetectionException("Git is not available on PATH. Change-scoped fusion requires git.");

        var startInfo = new ProcessStartInfo
        {
            FileName = gitPath,
            Arguments = $"diff --name-only {since}",
            WorkingDirectory = sourceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                throw new ChangeDetectionException("Failed to start git process.");
        }
        catch (Exception ex) when (ex is not ChangeDetectionException)
        {
            throw new ChangeDetectionException("Git is not available on PATH. Change-scoped fusion requires git.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            if (stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeDetectionException(
                    "Source directory is not a git repository. Change-scoped fusion requires a git repository.");
            }

            throw new ChangeDetectionException(string.IsNullOrWhiteSpace(stderr)
                ? $"git diff failed with exit code {process.ExitCode}."
                : stderr.Trim());
        }

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace('\\', '/'))
            .Where(line => line.Length > 0)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileDiff>> GetDiffsAsync(
        string sourceDirectory,
        string since,
        CancellationToken cancellationToken = default)
    {
        var stdout = await RunGitAsync(sourceDirectory, $"diff --unified=3 {since}", cancellationToken);
        return GitDiffParser.Parse(stdout);
    }

    // Runs git in the source directory and returns stdout, translating git failures into ChangeDetectionException
    // so callers see a single failure type regardless of which git command was run.
    private static async Task<string> RunGitAsync(string sourceDirectory, string arguments, CancellationToken cancellationToken)
    {
        var gitPath = FindGitExecutable();
        if (gitPath is null)
            throw new ChangeDetectionException("Git is not available on PATH. Change-scoped fusion requires git.");

        var startInfo = new ProcessStartInfo
        {
            FileName = gitPath,
            Arguments = arguments,
            WorkingDirectory = sourceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                throw new ChangeDetectionException("Failed to start git process.");
        }
        catch (Exception ex) when (ex is not ChangeDetectionException)
        {
            throw new ChangeDetectionException("Git is not available on PATH. Change-scoped fusion requires git.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            if (stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeDetectionException(
                    "Source directory is not a git repository. Change-scoped fusion requires a git repository.");
            }

            throw new ChangeDetectionException(string.IsNullOrWhiteSpace(stderr)
                ? $"git failed with exit code {process.ExitCode}."
                : stderr.Trim());
        }

        return stdout;
    }

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
