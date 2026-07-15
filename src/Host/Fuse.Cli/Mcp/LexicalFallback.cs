using System.Text;
using Fuse.Collection;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The opt-in inline lexical fallback (R30, <c>FUSE_LEXICAL_FALLBACK=1</c>, default off): when the index is
///     not semantic-ready and there is no native search to defer to (a fuse-only or CLI setup), a read serves a
///     scoped, ranked raw-text result instead of just a deferral signal. It is explicitly graded
///     <c>lexical-fallback</c> so an agent never mistakes it for a semantic answer. Honest bound: this does not
///     out-scan ripgrep at raw bytes; its win is scope (the repo's own tracked source, minus the R25 ignores) plus
///     the answer shape, and it always returns at least what a literal scan over that scope would.
/// </summary>
public static class LexicalFallback
{
    /// <summary>The environment variable that enables the inline lexical fallback.</summary>
    public const string EnvVar = "FUSE_LEXICAL_FALLBACK";

    private static readonly string[] TextExtensions =
        [".cs", ".csproj", ".props", ".targets", ".json", ".md", ".ts", ".tsx", ".js", ".py", ".xml", ".yml", ".yaml"];

    /// <summary>Whether the inline lexical fallback is enabled (default off; opt in with <c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>).</summary>
    /// <returns><see langword="true" /> when enabled.</returns>
    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        return value is not null
               && (value.Equals("1", StringComparison.Ordinal)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Runs a scoped, ranked literal-text search over the repo's own source (excluded and nested-VCS trees
    ///     pruned per R25), returning the top matches with a <c>lexical-fallback</c> grade header. Case-insensitive
    ///     substring line matching, ranked by matches per file.
    /// </summary>
    /// <param name="root">The workspace root.</param>
    /// <param name="query">The literal query text.</param>
    /// <param name="limit">The maximum number of hits to return.</param>
    /// <param name="cancellationToken">A token to cancel the search.</param>
    /// <returns>The graded fallback result.</returns>
    public static async Task<string> SearchAsync(string root, string query, int limit, CancellationToken cancellationToken)
    {
        var header =
            "grade: lexical-fallback (raw text matches, not semantic; the index is warming - retry fuse for a semantic answer)";
        if (string.IsNullOrWhiteSpace(query))
            return header + Environment.NewLine + "no query.";

        var fullRoot = Path.GetFullPath(root);
        var ignored = new HashSet<string>(WorkspaceExclusions.LoadDirectoryNames(fullRoot), StringComparer.OrdinalIgnoreCase);
        var perFile = new List<(string Path, int Count, string FirstHit)>();

        foreach (var file in EnumerateTextFiles(fullRoot, ignored, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var count = 0;
            var firstHit = string.Empty;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    if (firstHit.Length == 0)
                        firstHit = $"{Path.GetRelativePath(fullRoot, file).Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}";
                }
            }

            if (count > 0)
                perFile.Add((Path.GetRelativePath(fullRoot, file).Replace('\\', '/'), count, firstHit));
        }

        var builder = new StringBuilder();
        builder.AppendLine(header);
        builder.AppendLine($"lexical matches: {perFile.Count} file(s)");
        foreach (var hit in perFile.OrderByDescending(f => f.Count).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(limit))
            builder.AppendLine($"  {hit.FirstHit}  ({hit.Count} match(es) in {hit.Path})");
        return builder.ToString().TrimEnd();
    }

    // Bounded walk that prunes excluded directory names and nested version-control roots (R25), yielding only
    // text-extension files, so the scan stays over the repo's own source.
    private static IEnumerable<string> EnumerateTextFiles(string directory, HashSet<string> ignored, CancellationToken cancellationToken)
    {
        var root = directory;
        var stack = new Stack<string>();
        stack.Push(directory);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (ignored.Contains(Path.GetFileName(subdirectory)))
                    continue;
                if (!string.Equals(subdirectory, root, StringComparison.OrdinalIgnoreCase) && WorkspaceExclusions.IsVcsRoot(subdirectory))
                    continue;
                stack.Push(subdirectory);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }
}
