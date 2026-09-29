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
            throw HeadResolver.GitFailure(root, "git status", result);
        return [.. Paths(result.Output).Select(root.PathOf)];
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
