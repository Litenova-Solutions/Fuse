using Fuse.Fusion.Scoping;
using Fuse.Scoping;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Builds file-level out-edges from a <see cref="DependencyGraph" /> and computes normalized PageRank
///     centrality through the shared scoping module.
/// </summary>
public static class DependencyGraphCentrality
{
    /// <summary>
    ///     Computes normalized PageRank centrality in the range <c>[0, 1]</c> for every file in the dependency
    ///     graph that declares or references a type.
    /// </summary>
    /// <param name="graph">
    ///     The dependency graph. <see cref="DependencyGraph.DeclaredTypes" /> gives each file's declared types
    ///     and <see cref="DependencyGraph.TypeReferences" /> the files that reference each type; together they
    ///     define the file-to-file dependency edges PageRank runs over.
    /// </param>
    /// <returns>
    ///     A map from normalized relative path to its centrality score, scaled so the most central file is
    ///     <c>1.0</c>. An empty or edgeless graph yields an empty map (treated as zero by callers).
    /// </returns>
    public static IReadOnlyDictionary<string, double> Compute(DependencyGraph graph) =>
        GraphCentrality.NormalizedPageRank(BuildOutEdges(graph));

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildOutEdges(DependencyGraph graph)
    {
        var declarersByType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, declaredTypes) in graph.DeclaredTypes)
        {
            foreach (var typeName in declaredTypes)
            {
                if (!declarersByType.TryGetValue(typeName, out var list))
                {
                    list = [];
                    declarersByType[typeName] = list;
                }

                list.Add(path);
            }
        }

        // Seed every declaring file as a node (with an empty out-edge set), so an isolated file that declares a
        // type nothing references still receives a centrality score instead of being dropped from the result.
        var outEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in graph.DeclaredTypes.Keys)
            outEdges.TryAdd(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var (typeName, referrers) in graph.TypeReferences)
        {
            if (!declarersByType.TryGetValue(typeName, out var declarers))
                continue;

            foreach (var referrer in referrers)
            {
                foreach (var declarer in declarers)
                {
                    if (string.Equals(referrer, declarer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!outEdges.TryGetValue(referrer, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        outEdges[referrer] = set;
                    }

                    set.Add(declarer);
                }
            }
        }

        return outEdges.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
