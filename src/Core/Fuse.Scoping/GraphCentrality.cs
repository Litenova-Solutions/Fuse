namespace Fuse.Scoping;

/// <summary>
///     Shared graph-centrality helpers for scoping and retrieval ranking. PageRank scores file-level dependency
///     graphs during offline fusion focus expansion; normalized degree scores semantic node graphs during MCP
///     localization ranking.
/// </summary>
public static class GraphCentrality
{
    /// <summary>The maximum fractional boost a fully-central node receives in retrieval ranking.</summary>
    public const double RetrievalCentralityWeight = 0.10;

    private const double PageRankDamping = 0.85;
    private const int PageRankIterations = 20;

    /// <summary>
    ///     Computes normalized PageRank centrality in the range <c>[0, 1]</c> over the supplied out-edges.
    /// </summary>
    /// <param name="outEdges">Directed edges from each node to the nodes it depends on.</param>
    /// <returns>
    ///     A map from node key to its centrality score, scaled so the most central node is <c>1.0</c>. An
    ///     empty or edgeless graph yields an empty map.
    /// </returns>
    public static IReadOnlyDictionary<string, double> NormalizedPageRank(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> outEdges)
    {
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, targets) in outEdges)
        {
            nodes.Add(source);
            foreach (var target in targets)
                nodes.Add(target);
        }

        if (nodes.Count == 0 || outEdges.Count == 0)
            return Empty;

        var n = nodes.Count;
        var rank = new Dictionary<string, double>(n, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            rank[node] = 1.0 / n;

        var teleport = (1.0 - PageRankDamping) / n;
        for (var iter = 0; iter < PageRankIterations; iter++)
        {
            var next = new Dictionary<string, double>(n, StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
                next[node] = teleport;

            var danglingMass = 0.0;
            foreach (var node in nodes)
            {
                if (!outEdges.TryGetValue(node, out var targets) || targets.Count == 0)
                    danglingMass += rank[node];
            }

            var danglingShare = PageRankDamping * danglingMass / n;
            foreach (var node in nodes)
                next[node] += danglingShare;

            foreach (var (source, targets) in outEdges)
            {
                if (targets.Count == 0)
                    continue;

                var share = PageRankDamping * rank[source] / targets.Count;
                foreach (var target in targets)
                    next[target] += share;
            }

            rank = next;
        }

        var max = rank.Values.Max();
        if (max <= 0)
            return Empty;

        var centrality = new Dictionary<string, double>(rank.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, score) in rank)
            centrality[path] = score / max;

        return centrality;
    }

    /// <summary>
    ///     Computes normalized undirected node degree in the range <c>[0, 1]</c> from directed semantic edges.
    /// </summary>
    /// <param name="edges">Directed semantic edges; each endpoint contributes one degree count.</param>
    /// <returns>
    ///     A map from node id to its normalized degree. An empty edge set yields an empty map.
    /// </returns>
    public static IReadOnlyDictionary<string, double> NormalizedDegree(
        IEnumerable<(string From, string To)> edges)
    {
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (from, to) in edges)
        {
            degree[from] = degree.GetValueOrDefault(from) + 1;
            degree[to] = degree.GetValueOrDefault(to) + 1;
        }

        if (degree.Count == 0)
            return Empty;

        var max = degree.Values.Max();
        if (max == 0)
            return Empty;

        return degree.ToDictionary(kv => kv.Key, kv => (double)kv.Value / max, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Blends a traversal score with a query-independent centrality prior for expansion ranking.
    /// </summary>
    /// <param name="traversalScore">The relevance score propagated through the graph.</param>
    /// <param name="key">The node or file key to look up in <paramref name="centrality" />.</param>
    /// <param name="centrality">The normalized centrality map, or <c>null</c> to disable the prior.</param>
    /// <param name="weight">The prior weight; <c>0</c> returns <paramref name="traversalScore" /> unchanged.</param>
    /// <returns>The blended rank score.</returns>
    public static double BlendRankScore(
        double traversalScore,
        string key,
        IReadOnlyDictionary<string, double>? centrality,
        double weight)
    {
        if (weight == 0 || centrality is null)
            return traversalScore;

        return traversalScore + weight * centrality.GetValueOrDefault(key);
    }

    /// <summary>
    ///     Applies the capped retrieval-centrality multiplier to a candidate score.
    /// </summary>
    /// <param name="score">The candidate score before the prior.</param>
    /// <param name="centrality">The normalized node centrality in <c>[0, 1]</c>.</param>
    /// <returns>The boosted score, capped at <c>1.0</c>.</returns>
    public static double ApplyRetrievalPrior(double score, double centrality) =>
        Math.Min(1.0, score * (1.0 + RetrievalCentralityWeight * centrality));

    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}
