using System.Security.Cryptography;
using System.Text;
using Fuse.Failures;
using Fuse.Paths;

namespace Fuse.Repo;

/// <summary>
///     The tree stamp: a short hash of what the changed files hold, so a summary names the tree its command saw. The
///     engine stamps a check from its tracker and a build stamps from git; both hash the same way, so the same tree
///     stamps the same.
/// </summary>
internal static class TreeStamp
{
    /// <summary>
    ///     The stamp over <paramref name="files"/>: the first 12 lowercase hex characters of the SHA256 of the
    ///     <c>relative=SHA256(bytes)</c> lines in ordinal order, joined with <c>\n</c>. A file that is gone stamps as
    ///     <c>relative=deleted</c> and one that cannot be read as <c>relative=unreadable</c>, so the stamp never fails.
    /// </summary>
    public static string Of(IEnumerable<(string Relative, string Absolute)> files)
    {
        var lines = files.OrderBy(f => f.Relative, StringComparer.Ordinal).Select(Line).ToList();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..12];
    }

    /// <summary>
    ///     The stamp and the count of the C# and Razor sources git reports as changed, which is what a build and a
    ///     check stamp when they run from the command line, and what status stamps when no engine runs.
    /// </summary>
    /// <exception cref="FuseException">git cannot list the changes.</exception>
    public static async Task<(string Hash, int Count)> ForWorkingTreeAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        var changed = (await GitStatus.ChangedPathsAsync(root, cancellationToken).ConfigureAwait(false))
            .Where(path => PathRules.IsSource(path.Absolute))
            .ToList();
        return (Of(changed.Select(path => (path.Relative, path.Absolute))), changed.Count);
    }

    /// <summary>
    ///     The stamp as a check or build summary carries it, for example
    ///     <c>; tree 9f2c41aa07b3 over 87 changed file(s)</c>.
    /// </summary>
    public static string SummaryPart(string hash, int count) => $"; tree {hash} over {count} changed file(s)";

    private static string Line((string Relative, string Absolute) file)
    {
        string hash;
        try
        {
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Absolute)));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            hash = "deleted";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hash = "unreadable";
        }

        return $"{file.Relative}={hash}";
    }
}
