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
        var stdout = await RunGitAsync(sourceDirectory, cancellationToken, "diff", "--name-only", since);

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
        var stdout = await RunGitAsync(sourceDirectory, cancellationToken, "diff", "--unified=3", since);
        return GitDiffParser.Parse(stdout);
    }

    /// <inheritdoc />
    public async Task<string?> GetFileContentAtAsync(
        string sourceDirectory,
        string reference,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        // git show <ref>:<path> prints the file's content at that ref. A path absent at the ref (a newly added
        // file has no base version) exits non-zero with a "does not exist" / "exists on disk, but not in" message;
        // that is not an error for API-delta purposes - it means "no base symbols on this side", so return null.
        // Git being unavailable or the directory not being a repository still throws, matching the other methods.
        var spec = $"{reference}:{relativePath.Replace('\\', '/')}";
        var result = await RunGitRawAsync(sourceDirectory, cancellationToken, "show", spec);
        if (result.ExitCode == 0)
            return result.Stdout;

        if (result.Stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            result.Stderr.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase))
            return null;

        ThrowForGitFailure(result);
        return null; // unreachable: ThrowForGitFailure always throws on a non-zero exit.
    }

    // Runs git in the source directory and returns stdout, translating git failures into ChangeDetectionException
    // so callers see a single failure type regardless of which git command was run.
    private static async Task<string> RunGitAsync(string sourceDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await RunGitRawAsync(sourceDirectory, cancellationToken, arguments);
        if (result.ExitCode != 0)
            ThrowForGitFailure(result);

        return result.Stdout;
    }

    private static void ThrowForGitFailure(GitResult result)
    {
        if (result.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            throw new ChangeDetectionException(
                "Source directory is not a git repository. Change-scoped fusion requires a git repository.");
        }

        throw new ChangeDetectionException(string.IsNullOrWhiteSpace(result.Stderr)
            ? $"git failed with exit code {result.ExitCode}."
            : result.Stderr.Trim());
    }

    // Runs git and returns its exit code and captured streams without interpreting them, so callers can decide
    // whether a non-zero exit is an error (RunGitAsync) or an expected "not present" (GetFileContentAtAsync).
    // Arguments are passed as discrete tokens via ArgumentList so a ref containing spaces is never word-split
    // and nothing is shell-quoted.
    private static async Task<GitResult> RunGitRawAsync(string sourceDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var gitPath = GitExecutableLocator.Find();
        if (gitPath is null)
            throw new ChangeDetectionException("Git is not available on PATH. Change-scoped fusion requires git.");

        var startInfo = new ProcessStartInfo
        {
            FileName = gitPath,
            WorkingDirectory = sourceDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

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

        // Redirect and immediately close git's stdin so the child gets EOF, never the parent's inherited stdin.
        // Inside `fuse mcp serve` the parent's stdin is the live MCP client pipe; a git child that inherited it
        // could block (the write end stays open on the client side), which hung fuse_review while the CLI, whose
        // stdin is a real console, was unaffected. git never reads stdin for these commands, so closing it is safe.
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new GitResult(process.ExitCode, stdout, stderr);
    }

    // The exit code and captured streams of one git invocation.
    private readonly record struct GitResult(int ExitCode, string Stdout, string Stderr);
}
