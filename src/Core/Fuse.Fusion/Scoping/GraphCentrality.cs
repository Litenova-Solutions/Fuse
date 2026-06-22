namespace Fuse.Fusion.Scoping;

/// <summary>
///     Computes a cheap, query-independent importance prior over the files in a <see cref="DependencyGraph" />:
///     how depended-upon each file is. The result is a normalized in-degree (the share of other files that
///     reference the types a file declares), so the most architecturally central files can be nudged earlier
///     in ranking at equal relevance.
/// </summary>
/// <remarks>
///     This is in-degree centrality, not full PageRank: a file's score is the count of distinct other files
///     that reference any type it declares, divided by the maximum such count across the graph. It is computed
///     once per run from the already-built graph, so it adds no extra file reads. A file no one references
///     scores zero. Self-references do not count.
/// </remarks>
public static class GraphCentrality
{
    /// <summary>
    ///     Computes normalized in-degree centrality in the range <c>[0, 1]</c> for every file that declares a
    ///     referenced type.
    /// </summary>
    /// <param name="graph">The dependency graph whose reverse edges (<see cref="DependencyGraph.TypeReferences" />) define dependents.</param>
    /// <returns>
    ///     A map from normalized relative path to its centrality score. Files with no dependents are omitted
    ///     (treated as zero by callers). An empty graph yields an empty map.
    /// </returns>
    public static IReadOnlyDictionary<string, double> Compute(DependencyGraph graph)
    {
        var dependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // For each file, the distinct other files that reference any type it declares.
        foreach (var (path, declaredTypes) in graph.DeclaredTypes)
        {
            foreach (var typeName in declaredTypes)
            {
                if (!graph.TypeReferences.TryGetValue(typeName, out var referencingPaths))
                    continue;

                foreach (var referrer in referencingPaths)
                {
                    if (string.Equals(referrer, path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!dependents.TryGetValue(path, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        dependents[path] = set;
                    }

                    set.Add(referrer);
                }
            }
        }

        if (dependents.Count == 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var max = dependents.Values.Max(s => s.Count);
        if (max == 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var centrality = new Dictionary<string, double>(dependents.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, set) in dependents)
            centrality[path] = (double)set.Count / max;

        return centrality;
    }
}
