using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     The retrieval engine: localizes a task to ranked candidates, and plans a context payload by expanding
///     seeds across the semantic graph and packing the result under a token budget.
/// </summary>
/// <remarks>
///     This replaces the old query-scoping pipeline. Localization is candidate generation plus normalization
///     (no graph walk, no bodies). Context planning resolves seeds to graph nodes, expands typed edges, collapses
///     nodes to files, assigns a role and render tier per file, and greedily packs must-keep files first.
/// </remarks>
public sealed class SemanticRetrievalEngine
{
    private const double ExpansionThreshold = 0.10;

    private readonly IWorkspaceIndexStore _store;
    private readonly CandidateGenerator _candidateGenerator;
    private readonly CandidateScorer _scorer;
    private readonly GraphExpansionEngine _expansion;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticRetrievalEngine" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    /// <param name="changeSource">An optional change source so <c>ChangedSince</c> resolves to changed-file seeds.</param>
    public SemanticRetrievalEngine(IWorkspaceIndexStore store, IChangeSource? changeSource = null)
    {
        _store = store;
        _candidateGenerator = CandidateGenerator.CreateDefault(store, changeSource);
        _scorer = new CandidateScorer();
        _expansion = new GraphExpansionEngine(store, new EdgeWeightProvider());
    }

    /// <summary>
    ///     Localizes a task to ranked candidate files and symbols, with no source bodies.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The ranked candidates and any warnings.</returns>
    public async Task<LocalizationResult> LocalizeAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        var candidates = await _candidateGenerator.GenerateAsync(request, cancellationToken);
        var scored = _scorer.Score(candidates);

        var localized = new List<LocalizedCandidate>(scored.Count);
        foreach (var candidate in scored.Take(request.MaxCandidates))
        {
            var tokens = await _store.GetFileTokenEstimateAsync(candidate.FilePath, cancellationToken);
            localized.Add(new LocalizedCandidate(
                candidate.FilePath, candidate.NodeId, candidate.Kind, candidate.Score, tokens, candidate.Reasons));
        }

        var warnings = new List<string>();
        if (localized.Count == 0)
            warnings.Add("Low signal: no candidates found. Provide a route, symbol, service, request, config section, or a git base.");

        return new LocalizationResult(localized, warnings);
    }

    /// <summary>
    ///     Plans a context payload around a set of seeds.
    /// </summary>
    /// <param name="request">The context request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The context plan.</returns>
    public async Task<ContextPlan> PlanContextAsync(ContextRequest request, CancellationToken cancellationToken)
    {
        var seedCandidates = await BuildSeedCandidatesAsync(request.Seeds, cancellationToken);
        var scoredSeeds = _scorer.Score(seedCandidates);
        var expanded = await _expansion.ExpandAsync(scoredSeeds, request.Depth, ExpansionThreshold, cancellationToken);

        // Collapse nodes to files; the best-scoring node for a file decides its role and provenance.
        var byFile = expanded
            .Where(n => !string.IsNullOrEmpty(n.FilePath))
            .GroupBy(n => n.FilePath!, StringComparer.Ordinal);

        var items = new List<ContextPlanItem>();
        var warnings = new List<string>();
        foreach (var group in byFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = group.OrderByDescending(n => n.Score).First();
            var role = RoleFor(best);
            if (role == "test" && !request.IncludeTests)
                continue;
            if (role == "config" && !request.IncludeConfig)
                continue;

            var tokens = await _store.GetFileTokenEstimateAsync(group.Key, cancellationToken);
            items.Add(new ContextPlanItem(
                Path: group.Key,
                NodeId: string.IsNullOrEmpty(best.NodeId) ? null : best.NodeId,
                Role: role,
                Tier: TierFor(role, request.RenderMode),
                Score: best.Score,
                EstimatedTokens: tokens,
                MustKeep: group.Any(n => n.MustKeep),
                Reasons: best.Provenance,
                ProvenanceChain: best.Provenance));
        }

        var packed = Pack(items, request.MaxTokens, warnings);
        var estimated = packed.Sum(i => i.EstimatedTokens);
        return new ContextPlan("context", packed, [], estimated, warnings);
    }

    private async Task<List<CandidateNode>> BuildSeedCandidatesAsync(IReadOnlyList<ContextSeed> seeds, CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateNode>();
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (seed.Kind)
            {
                case ContextSeedKind.File:
                    candidates.Add(new CandidateNode(
                        string.Empty, seed.Value.Replace('\\', '/'), "file", 1.0,
                        CandidateSource.DiffChangedFile, ["seed: file"], 0));
                    break;
                case ContextSeedKind.Route:
                    await AddRouteSeedAsync(candidates, seed.Value, cancellationToken);
                    break;
                default:
                    await AddNamedSeedAsync(candidates, seed.Value, cancellationToken);
                    break;
            }
        }

        return candidates;
    }

    private async Task AddNamedSeedAsync(List<CandidateNode> candidates, string value, CancellationToken cancellationToken)
    {
        var nodes = await _store.FindNodesByDisplayNameAsync(SimpleName(value), cancellationToken);
        foreach (var node in nodes)
        {
            candidates.Add(new CandidateNode(
                node.NodeId, node.FilePath ?? string.Empty, node.Kind, 1.0,
                CandidateSource.SymbolExact, [$"seed: {value}"], 0));
        }
    }

    private async Task AddRouteSeedAsync(List<CandidateNode> candidates, string route, CancellationToken cancellationToken)
    {
        var (method, pattern) = ParseRoute(route);
        var routeNodeId = $"route:{method}:{pattern}";
        var node = await _store.GetNodeAsync(routeNodeId, cancellationToken);
        if (node is not null)
        {
            candidates.Add(new CandidateNode(
                node.NodeId, node.FilePath ?? string.Empty, node.Kind, 1.0,
                CandidateSource.RouteExact, [$"seed: route {route}"], 0));
        }
    }

    // Greedily include must-keep files first, then by score, until the budget is reached. Must-keep files are
    // always included; optional files past the budget are dropped with a warning.
    private static List<ContextPlanItem> Pack(List<ContextPlanItem> items, int? maxTokens, List<string> warnings)
    {
        var ordered = items
            .OrderByDescending(i => i.MustKeep)
            .ThenByDescending(i => i.Score)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        if (maxTokens is not { } budget)
            return ordered;

        var kept = new List<ContextPlanItem>();
        var used = 0;
        var dropped = 0;
        foreach (var item in ordered)
        {
            if (item.MustKeep || used + item.EstimatedTokens <= budget)
            {
                kept.Add(item);
                used += item.EstimatedTokens;
            }
            else
            {
                dropped++;
            }
        }

        if (dropped > 0)
            warnings.Add($"{dropped} file(s) dropped to fit the {budget} token budget.");

        return kept;
    }

    private static string RoleFor(ExpandedNode node)
    {
        if (node.MustKeep)
            return "exact-seed";

        var edgeType = LastEdgeType(node.Provenance);
        return edgeType switch
        {
            "route_handles" => "route-handler",
            "mediatr_handles" => "request-handler",
            "di_resolves_to" or "di_depends_on_impl" => "di-implementation",
            "di_injects" or "sends_request" => "consumer",
            "options_binds" or "options_consumes" or "config_impacts" => "config",
            "tests" => "test",
            _ => "dependency",
        };
    }

    private static RenderTier TierFor(string role, ContextRenderMode mode) => mode switch
    {
        ContextRenderMode.Source => RenderTier.FullSource,
        ContextRenderMode.Reduced => RenderTier.Reduced,
        ContextRenderMode.Skeleton => RenderTier.Skeleton,
        ContextRenderMode.PublicApi => RenderTier.PublicApi,
        _ => role switch
        {
            "exact-seed" or "route-handler" or "request-handler" or "di-implementation" => RenderTier.Reduced,
            "config" => RenderTier.FullSource,
            "consumer" or "test" or "dependency" => RenderTier.Skeleton,
            _ => RenderTier.Skeleton,
        },
    };

    private static string? LastEdgeType(IReadOnlyList<string> provenance)
    {
        for (var i = provenance.Count - 1; i >= 0; i--)
        {
            // Provenance entries from expansion read "-> edge_type (hop n)" or "<- edge_type (hop n)".
            var parts = provenance[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && (parts[0] == "->" || parts[0] == "<-"))
                return parts[1];
        }

        return null;
    }

    private static string SimpleName(string name)
    {
        var trimmed = name.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    private static (string Method, string Pattern) ParseRoute(string route)
    {
        var trimmed = route.Trim();
        var space = trimmed.IndexOf(' ');
        if (space < 0)
            return ("GET", Normalize(trimmed));
        return (trimmed[..space].Trim().ToUpperInvariant(), Normalize(trimmed[(space + 1)..].Trim()));
    }

    private static string Normalize(string pattern)
    {
        if (pattern.Length == 0)
            return "/";
        if (!pattern.StartsWith('/'))
            pattern = "/" + pattern;
        return pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;
    }
}
