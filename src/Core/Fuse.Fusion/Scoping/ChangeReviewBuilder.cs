using System.Text;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Builds the review map prepended to review-shaped change emission: each changed file's diff hunks paired
///     with its direct callers.
/// </summary>
/// <remarks>
///     The map answers the two questions a reviewer asks first: what changed (the inline hunks) and what could
///     break (the files that reference the changed code). Caller resolution rests on the same best-effort
///     dependency graph as the rest of scoping, so it can miss dynamically dispatched references.
/// </remarks>
public static class ChangeReviewBuilder
{
    /// <summary>
    ///     Renders the review map for the supplied changed files.
    /// </summary>
    /// <param name="diffs">The diff hunks for each changed file, keyed by normalized relative path.</param>
    /// <param name="callersByPath">The direct callers of each changed file, keyed by normalized relative path.</param>
    /// <returns>
    ///     A comment-wrapped review map terminated by a newline, or <see cref="string.Empty" /> when there are
    ///     no diffs to show.
    /// </returns>
    public static string Build(
        IReadOnlyList<FileDiff> diffs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> callersByPath)
    {
        if (diffs.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<!-- fuse:review ").Append(diffs.Count).Append(" changed file(s).\n");
        sb.Append("     Each file shows its diff hunks and the direct callers that may be affected. -->\n");

        foreach (var diff in diffs.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("\n=== review: ").Append(diff.Path)
                .Append(" (+").Append(diff.Added).Append(" -").Append(diff.Removed).Append(") ===\n");

            if (callersByPath.TryGetValue(diff.Path, out var callers) && callers.Count > 0)
                sb.Append("callers (").Append(callers.Count).Append("): ")
                    .Append(string.Join(", ", callers.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))).Append('\n');
            else
                sb.Append("callers: none detected\n");

            if (!string.IsNullOrEmpty(diff.Hunks))
                sb.Append(diff.Hunks).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Computes the direct callers of each changed file: the files that reference any type the changed file
    ///     declares, excluding the file itself.
    /// </summary>
    /// <param name="changedPaths">The normalized relative paths of the changed files.</param>
    /// <param name="graph">The dependency graph providing declared types and reverse edges.</param>
    /// <returns>A map from each changed path to the sorted distinct paths of its direct callers.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ComputeCallers(
        IEnumerable<string> changedPaths,
        DependencyGraph graph)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in changedPaths)
        {
            var callers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (graph.DeclaredTypes.TryGetValue(path, out var declaredTypes))
            {
                foreach (var type in declaredTypes)
                {
                    if (!graph.TypeReferences.TryGetValue(type, out var referencing))
                        continue;

                    foreach (var referrer in referencing)
                    {
                        if (!string.Equals(referrer, path, StringComparison.OrdinalIgnoreCase))
                            callers.Add(referrer);
                    }
                }
            }

            result[path] = callers.ToArray();
        }

        return result;
    }
}
