using System.Diagnostics;
using System.Text;
using Fuse.Dotnet;
using Fuse.Failures;
using Fuse.Paths;

namespace Fuse.Repo;

/// <summary>
///     Reads which files differ from HEAD from <c>git status</c>. The tracker seeds its set of changed files from it when
///     it starts and again after a sync it cannot follow file by file; between those, the file watcher keeps the set.
/// </summary>
internal static class GitStatus
{
    /// <summary>Every file git reports as modified, added, deleted or untracked. Ignored files are left out.</summary>
    /// <exception cref="FuseException">git status fails.</exception>
    public static async Task<IReadOnlyList<RepoPath>> ChangedPathsAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        // -z gives one NUL-separated record per change with the path exactly as it is on disk. Without it git C-quotes a
        // path that holds a quote, a backslash or a control character, and the two are not the same string: the path is
        // read from the repository, not from git's spelling of it.
        var result = await ProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotepath=off", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignored=no"],
            root.Path,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw HeadResolver.GitFailure(root, "listing changed files", result);
        return [.. Paths(result.Output).Select(root.PathOf)];
    }

    /// <summary>The paths among <paramref name="paths"/> that git ignores, read in one batched call.</summary>
    /// <remarks>
    ///     The paths go to <c>git check-ignore</c> on its standard input as NUL-separated repository-relative paths, so a sync with many watcher reports starts git once and no path passes through a shell or a command line. A tracked file is not reported, because git does not ignore a file it tracks. When git cannot answer, no path is reported, so a change is counted rather than hidden.
    /// </remarks>
    public static async Task<HashSet<RepoPath>> IgnoredPathsAsync(RepoRoot root, IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        var candidates = paths.Where(p => root.Contains(p.Absolute)).ToList();
        if (candidates.Count == 0)
            return [];
        var byRelative = candidates.ToDictionary(p => p.Relative, p => p, StringComparer.Ordinal);
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("check-ignore");
        psi.ArgumentList.Add("--stdin");
        psi.ArgumentList.Add("-z");
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return [];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }

        var reading = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            var input = Encoding.UTF8.GetBytes(string.Join('\0', candidates.Select(p => p.Relative)) + '\0');
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        if (process.ExitCode is not (0 or 1))
            return [];
        var output = await reading.ConfigureAwait(false);
        var ignored = new HashSet<RepoPath>();
        foreach (var relative in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (byRelative.TryGetValue(relative, out var path))
                ignored.Add(path);
        }

        return ignored;
    }

    /// <summary>The repository-relative path in each record of <c>git status --porcelain=v1 -z</c> output.</summary>
    /// <remarks>
    ///     Each record is two status characters and a space, then the path. Nothing is trimmed off the path: a name may
    ///     begin or end with a space, and trimming it would name a file that is not there. A record too short to hold a
    ///     path, such as the line break the process runner appends after the last record, is skipped.
    /// </remarks>
    public static IEnumerable<string> Paths(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(r => r.Length >= 4).Select(r => r[3..]);
}
