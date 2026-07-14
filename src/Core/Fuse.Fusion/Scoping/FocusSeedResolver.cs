using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Scoping;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Resolves focus seeds to file paths and expands dependency scopes.
/// </summary>
public sealed class FocusSeedResolver
{
    private readonly CapabilityRegistry<ITypeNameLocator> _typeLocators;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FocusSeedResolver" /> class.
    /// </summary>
    /// <param name="typeLocators">Registry of per-extension locators used to resolve a type-name seed to its defining files.</param>
    public FocusSeedResolver(CapabilityRegistry<ITypeNameLocator> typeLocators)
    {
        _typeLocators = typeLocators;
    }

    /// <summary>
    ///     Resolves a seed string to matching file paths using path, filename, type name, and directory
    ///     prefix strategies.
    /// </summary>
    /// <param name="seed">The seed to resolve: a relative path, filename, type name, or directory prefix.</param>
    /// <param name="files">The candidate source files to match against.</param>
    /// <param name="contentProvider">Provider used to read file content when resolving a type-name seed.</param>
    /// <param name="cancellationToken">Token used to cancel content reads.</param>
    /// <returns>
    ///     The awaited result is the set of normalized relative paths matched by the seed, using a
    ///     case-insensitive comparer. Empty when nothing matches.
    /// </returns>
    /// <remarks>
    ///     Strategies are tried in order and the first that yields any match wins: exact path, then exact
    ///     filename, then files defining a type of that name, then directory prefix. Only the type-name
    ///     strategy reads file content.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is cancelled.</exception>
    public async Task<HashSet<string>> ResolveSeedPathsAsync(
        string seed,
        IReadOnlyList<SourceFile> files,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken = default)
    {
        var normalizedSeed = seed.Replace('\\', '/').Trim('/');
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (string.Equals(file.NormalizedRelativePath, normalizedSeed, StringComparison.OrdinalIgnoreCase))
                result.Add(file.NormalizedRelativePath);
        }

        if (result.Count > 0)
            return result;

        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file.NormalizedRelativePath), normalizedSeed, StringComparison.OrdinalIgnoreCase))
                result.Add(file.NormalizedRelativePath);
        }

        if (result.Count > 0)
            return result;

        foreach (var file in files)
        {
            var locator = _typeLocators.TryResolve(file.Extension);
            if (locator is null)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var content = await contentProvider.GetContentAsync(file, cancellationToken);
            if (locator.ContainsTypeDefinition(content, normalizedSeed))
                result.Add(file.NormalizedRelativePath);
        }

        if (result.Count > 0)
            return result;

        var prefix = normalizedSeed.EndsWith('/') ? normalizedSeed : normalizedSeed + "/";
        foreach (var file in files)
        {
            if (file.NormalizedRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result.Add(file.NormalizedRelativePath);
        }

        return result;
    }

    /// <summary>
    ///     Expands seed paths by breadth-first traversal up to the specified depth using forward edges only,
    ///     scoring every seed equally. Retained for callers that do not need reverse edges or budget gating.
    /// </summary>
    /// <param name="graph">The dependency graph used to follow references from each file.</param>
    /// <param name="seedPaths">The starting set of normalized relative paths.</param>
    /// <param name="depth">The maximum number of hops to traverse from any seed; <c>0</c> returns only the seeds.</param>
    /// <returns>The expansion result. See <see cref="PathExpansionResult" />.</returns>
    public PathExpansionResult ExpandPaths(DependencyGraph graph, HashSet<string> seedPaths, int depth) =>
        Expand(
            graph,
            seedPaths.ToDictionary(p => p, _ => 1.0, StringComparer.OrdinalIgnoreCase),
            new ExpansionOptions(depth));

    /// <summary>
    ///     Expands scored seeds across the dependency graph using a best-first, rank-decayed traversal that
    ///     can follow forward edges (dependencies), reverse edges (dependents), or both, and that can stop
    ///     once an optional token budget is reached.
    /// </summary>
    /// <param name="graph">The dependency graph used to follow edges from each file.</param>
    /// <param name="seedScores">
    ///     The starting paths mapped to their seed scores. Stronger seeds expand first, so under a budget
    ///     more of their neighbourhood is admitted before weaker seeds are reached.
    /// </param>
    /// <param name="options">The traversal controls. See <see cref="ExpansionOptions" />.</param>
    /// <returns>
    ///     The included paths, a provenance chain for each (the hop sequence from a seed to its inclusion),
    ///     and a relevance score for each. See <see cref="PathExpansionResult" />.
    /// </returns>
    /// <remarks>
    ///     Seeds are always admitted, even past the budget, because they are the explicit request. Neighbours
    ///     are admitted highest-score first; a neighbour's score is its parent's score multiplied by
    ///     <see cref="ExpansionOptions.HopDecay" />. Each path is admitted once, with the highest score and
    ///     shortest chain by which it is reached. Traversal follows the best-effort edges in the graph, so the
    ///     expansion inherits the graph's false-positive and missed-edge characteristics.
    /// </remarks>
    public PathExpansionResult Expand(
        DependencyGraph graph,
        IReadOnlyDictionary<string, double> seedScores,
        ExpansionOptions options)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var queue = new PriorityQueue<FrontierNode, (double NegScore, string Path)>(FrontierComparer);
        var budgetUsed = 0;

        // Admit all seeds first, ordered by rank score (relevance blended with the centrality prior),
        // strongest first, regardless of budget. The propagated score stays centrality-free so the prior
        // never compounds across hops.
        foreach (var seed in seedScores
                     .OrderByDescending(s => RankScore(options, s.Key, s.Value))
                     .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!included.Add(seed.Key))
                continue;

            provenance[seed.Key] = [seed.Key];
            scores[seed.Key] = RankScore(options, seed.Key, seed.Value);
            budgetUsed += Cost(options, seed.Key);

            if (options.Depth > 0)
                EnqueueNeighbours(graph, queue, options, seed.Key, [seed.Key], seed.Value, hop: 1);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!included.Contains(node.Path))
            {
                var cost = Cost(options, node.Path);
                if (options.TokenBudget is { } budget && budgetUsed + cost > budget)
                    continue;

                included.Add(node.Path);
                provenance[node.Path] = node.Chain;
                // Record the rank score (relevance + centrality prior); propagate the centrality-free score.
                scores[node.Path] = RankScore(options, node.Path, node.Score);
                budgetUsed += cost;

                if (node.Hop < options.Depth)
                    EnqueueNeighbours(graph, queue, options, node.Path, node.Chain, node.Score, node.Hop + 1);
            }
        }

        return new PathExpansionResult(included, provenance, scores);
    }

    // Relevance blended with the query-independent centrality prior. Additive and zero at CentralityWeight 0,
    // so a zero weight reproduces the prior ordering exactly.
    private static double RankScore(ExpansionOptions options, string path, double traversalScore)
    {
        return GraphCentrality.BlendRankScore(traversalScore, path, options.Centrality, options.CentralityWeight);
    }

    private static void EnqueueNeighbours(
        DependencyGraph graph,
        PriorityQueue<FrontierNode, (double NegScore, string Path)> queue,
        ExpansionOptions options,
        string path,
        IReadOnlyList<string> parentChain,
        double parentScore,
        int hop)
    {
        var nextScore = parentScore * options.HopDecay;

        if (options.FollowReferences && graph.FileReferences.TryGetValue(path, out var referencedTypes))
        {
            foreach (var typeName in referencedTypes)
            {
                if (graph.TypeIndex.TryGetValue(typeName, out var definingPaths))
                    EnqueueEach(queue, options, definingPaths, path, parentChain, nextScore, hop);
            }
        }

        if (options.FollowDependents && graph.DeclaredTypes.TryGetValue(path, out var declaredTypes))
        {
            foreach (var typeName in declaredTypes)
            {
                if (graph.TypeReferences.TryGetValue(typeName, out var referencingPaths))
                    EnqueueEach(queue, options, referencingPaths, path, parentChain, nextScore, hop);
            }
        }

        // Structural proximity edges (item 7): a test or implementation counterpart and same-stem siblings,
        // followed at a weight below a real reference, so they break ties and reach a related file the
        // type-reference graph missed without overrunning the budget.
        if (options.ProximityWeight > 0 && options.ProximityEdges is { } proximity &&
            proximity.TryGetValue(path, out var nearbyPaths))
        {
            EnqueueEach(queue, options, nearbyPaths, path, parentChain, nextScore * options.ProximityWeight, hop);
        }
    }

    private static void EnqueueEach(
        PriorityQueue<FrontierNode, (double NegScore, string Path)> queue,
        ExpansionOptions options,
        IReadOnlyList<string> neighbours,
        string parentPath,
        IReadOnlyList<string> parentChain,
        double score,
        int hop)
    {
        foreach (var neighbour in neighbours)
        {
            if (string.Equals(neighbour, parentPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var chain = new List<string>(parentChain) { neighbour };
            // The node carries the centrality-free traversal score for propagation; the queue is ordered by the
            // rank score so the centrality prior influences which neighbours win admission under a budget.
            queue.Enqueue(new FrontierNode(neighbour, chain, score, hop), (-RankScore(options, neighbour, score), neighbour));
        }
    }

    private static int Cost(ExpansionOptions options, string path) =>
        options.TokenBudget is null || options.TokenCosts is null
            ? 0
            : options.TokenCosts.GetValueOrDefault(path);

    private static readonly IComparer<(double NegScore, string Path)> FrontierComparer = new FrontierPriorityComparer();

    private sealed record FrontierNode(string Path, IReadOnlyList<string> Chain, double Score, int Hop);

    private sealed class FrontierPriorityComparer : IComparer<(double NegScore, string Path)>
    {
        public int Compare((double NegScore, string Path) x, (double NegScore, string Path) y)
        {
            var byScore = x.NegScore.CompareTo(y.NegScore);
            return byScore != 0 ? byScore : string.CompareOrdinal(x.Path, y.Path);
        }
    }
}
