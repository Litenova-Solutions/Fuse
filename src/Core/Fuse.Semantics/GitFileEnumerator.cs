using System.Diagnostics;

namespace Fuse.Semantics;

/// <summary>
///     Lists a git working tree's files (tracked, plus untracked but not ignored) so the scanner can enumerate a
///     git repository the way git itself sees it. Other worktrees, embedded repositories, and ignored trees are
///     excluded by construction, which is the authoritative defense against indexing duplicate or foreign content.
/// </summary>
/// <remarks>
///     Best-effort by design: a missing git executable, a directory that is not a work tree, or any failing or
///     slow git call yields null, so the caller falls back to the directory walk. The single <c>git ls-files</c>
///     invocation has a fixed argument list (no variable path list), so the external-process command line is
///     bounded, honoring the bounded-args invariant.
/// </remarks>
public sealed class GitFileEnumerator
{
    // A hard ceiling so a stuck git subprocess (a pager, a credential prompt, a lock) cannot hang indexing.
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Lists the working-tree files of a git repository as paths relative to <paramref name="rootDirectory" />.
    /// </summary>
    /// <param name="rootDirectory">The candidate repository root.</param>
    /// <param name="cancellationToken">A token to cancel the git call.</param>
    /// <returns>
    ///     The relative file paths, or null when the directory is not a git work tree or git is unavailable, so the
    ///     caller can fall back to a directory walk.
    /// </returns>
    public async Task<IReadOnlyList<string>?> TryListAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);

        // -z gives NUL-separated output so paths with spaces or newlines stay unambiguous. --cached lists tracked
        // files; --others adds untracked files (new sources not yet committed); --exclude-standard applies
        // .gitignore, .git/info/exclude, and the global excludes; --deduplicate avoids listing a path twice.
        var output = await RunGitAsync(
            root,
            ["-c", "core.quotepath=false", "ls-files", "--cached", "--others", "--exclude-standard", "--deduplicate", "-z"],
            cancellationToken);
        if (output is null)
            return null;

        var files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        // A repository with no listable files falls back to the walk (which will also find nothing); a populated
        // list drives the git-native enumeration in the collection pipeline.
        return files.Length == 0 ? null : files;
    }

    // Runs git with a separated argument list (no shell quoting, no command-line concatenation), capturing stdout.
    // Returns null on any failure or non-zero exit so the caller degrades to the directory walk.
    private static async Task<string?> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";

        using var timeout = new CancellationTokenSource(GitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            _ = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            return process.ExitCode == 0 ? await stdoutTask : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
