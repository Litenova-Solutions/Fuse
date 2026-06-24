namespace Fuse.Fusion.Scoping;

/// <summary>
///     Computes a cheap, query-independent importance prior over the files in a <see cref="DependencyGraph" />:
///     how depended-upon each file is, by PageRank. The result is normalized to <c>[0, 1]</c>, so the most
///     architecturally central files can be nudged earlier in ranking at equal relevance.
/// </summary>
/// <remarks>
///     This is PageRank, not raw in-degree: importance flows from a file to the files it depends on (the files
///     declaring the types it references), so a type referenced by many already-central files inherits more
///     weight than a count of distinct referrers would give it. It is computed once per run from the
///     already-built graph by a few power-iteration passes, so it adds no file reads. A file no one references,
///     and that references nothing depended-upon, settles near the uniform floor.
/// </remarks>
public static class GraphCentrality
{
    private const double Damping = 0.85;
    private const int Iterations = 20;

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
    public static IReadOnlyDictionary<string, double> Compute(DependencyGraph graph)
    {
        // Invert DeclaredTypes so a referenced type name resolves to the file(s) that declare it.
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

        // Out-edges: a file points to every file declaring a type it references (it "depends on" that file), so
        // importance accumulates at depended-upon files. Self-edges are dropped.
        var outEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in graph.DeclaredTypes.Keys)
            nodes.Add(path);

        foreach (var (typeName, referrers) in graph.TypeReferences)
        {
            if (!declarersByType.TryGetValue(typeName, out var declarers))
                continue;

            foreach (var referrer in referrers)
            {
                nodes.Add(referrer);
                foreach (var declarer in declarers)
                {
                    if (string.Equals(referrer, declarer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    nodes.Add(declarer);
                    if (!outEdges.TryGetValue(referrer, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        outEdges[referrer] = set;
                    }

                    set.Add(declarer);
                }
            }
        }

        if (nodes.Count == 0 || outEdges.Count == 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var n = nodes.Count;
        var rank = new Dictionary<string, double>(n, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            rank[node] = 1.0 / n;

        var teleport = (1.0 - Damping) / n;
        for (var iter = 0; iter < Iterations; iter++)
        {
            var next = new Dictionary<string, double>(n, StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
                next[node] = teleport;

            // Dangling nodes (no out-edges) would leak their mass; redistribute it uniformly each pass so the
            // ranks keep summing to one.
            var danglingMass = 0.0;
            foreach (var node in nodes)
            {
                if (!outEdges.TryGetValue(node, out var targets) || targets.Count == 0)
                    danglingMass += rank[node];
            }

            var danglingShare = Damping * danglingMass / n;
            foreach (var node in nodes)
                next[node] += danglingShare;

            foreach (var (source, targets) in outEdges)
            {
                var share = Damping * rank[source] / targets.Count;
                foreach (var target in targets)
                    next[target] += share;
            }

            rank = next;
        }

        var max = rank.Values.Max();
        if (max <= 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var centrality = new Dictionary<string, double>(rank.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, score) in rank)
            centrality[path] = score / max;

        return centrality;
    }
}
