using System.Globalization;

namespace Fuse.Fusion;

/// <summary>
///     Tunable, experimental scoring knobs whose defaults may change between releases. Carried explicitly on a
///     <see cref="FusionRequest" /> so a run's configuration is part of the request rather than ambient process
///     state, and recorded in the machine-readable run report so a committed measurement is self-describing.
/// </summary>
/// <remarks>
///     Environment variables override the configured values (see <see cref="ResolveFromEnvironment" />) so an
///     operator can A/B a knob without rebuilding, but the override is applied on top of the request's values
///     rather than read from ambient state at the point of use. This keeps a result reproducible: the resolved
///     options describe exactly what produced it.
/// </remarks>
public sealed record ExperimentalOptions
{
    /// <summary>
    ///     The query-independent graph-centrality weight blended into seed and expansion scores, so at equal
    ///     relevance the more depended-upon file ranks earlier. <c>0</c> disables the prior. Overridden by
    ///     <c>FUSE_CENTRALITY_WEIGHT</c>.
    /// </summary>
    public double CentralityWeight { get; init; } = 0.15;

    /// <summary>
    ///     Whether pseudo-relevance feedback query expansion runs on the query path (a second lexical ranking
    ///     pass seeded with recurring declared-symbol terms from the first pass). Overridden by
    ///     <c>FUSE_QUERY_EXPANSION</c> (<c>0</c>, <c>off</c>, or <c>false</c> disables it).
    /// </summary>
    public bool QueryExpansion { get; init; } = true;

    /// <summary>
    ///     Whether tiered emission is on for query and focus scoping: dependency-expanded neighbour files
    ///     (provenance hop two or deeper) are reduced to signature skeletons rather than full bodies, so each
    ///     costs fewer tokens and the budget-aware packer fits more files. Seeds keep their planned tier.
    ///     Recall counts file presence, so fitting more truth files raises recall, most on large change sets.
    ///     On by default: an A/B over the pinned corpus lifted query recall at 50k from 51 to 55 percent and
    ///     focus from 71 to 77 percent at fewer tokens, with no per-repo regression. Set
    ///     <c>FUSE_TIERED_EMISSION=0</c> (or <c>off</c>/<c>false</c>) to reproduce the untiered ordering.
    /// </summary>
    public bool TieredEmission { get; init; } = true;

    /// <summary>
    ///     Whether the query path fuses several query variants (the raw query, an identifier-only subset, and
    ///     the pseudo-relevance-expanded query) with Reciprocal Rank Fusion instead of the single-pass plus
    ///     seed-preserving PRF merge. Off pending an A/B. Overridden by <c>FUSE_QUERY_FUSION</c> (<c>1</c>,
    ///     <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool MultiQueryFusion { get; init; }

    /// <summary>
    ///     Whether dependency expansion on the query path is budget-aware: when a token ceiling is set, the
    ///     graph admits neighbours highest-score first only while their estimated reduced cost fits the budget,
    ///     instead of admitting the whole neighbourhood and leaving the packer to cut it. The per-file estimate
    ///     comes from the <see cref="Scoping.ITokenCostModel" /> at the level each file will be emitted at
    ///     (skeleton for neighbours when <see cref="TieredEmission" /> is on, the request level otherwise), so a
    ///     cheap skeletonized neighbour is not rejected as if it cost a full body. Seeds are always admitted.
    ///     This saves reducing files that would never emit and keeps the admitted set aligned with what the
    ///     packer keeps. On by default; set <c>FUSE_BUDGET_EXPANSION=0</c> (or <c>off</c>/<c>false</c>) to
    ///     reproduce the unbounded expansion.
    /// </summary>
    public bool BudgetAwareExpansion { get; init; } = true;

    /// <summary>
    ///     Whether the query path reranks its BM25 candidate pool with a dense embedding model, blending the
    ///     lexical score with query-to-document similarity so a semantically matching file can outrank one that
    ///     merely shares words. Requires a registered <see cref="Scoping.IReranker" /> with a present model; when
    ///     no reranker is available (no model, offline, or absent assembly) the query path stays on the lexical
    ///     BM25F floor regardless of this flag. Off by default; overridden by <c>FUSE_RERANK</c> (<c>1</c>,
    ///     <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool DenseRerank { get; init; }

    /// <summary>
    ///     The weight of a git churn and recency prior blended as a multiplier into the query candidate scores,
    ///     so a recently and frequently changed file ranks a little higher. <c>0</c> (the default) disables it.
    ///     Overridden by <c>FUSE_GIT_CHURN_WEIGHT</c>. This is a production-routing lever: the pinned benchmark
    ///     corpus checks out historical PR-head commits, where recent-churn-from-now is uniformly empty and a
    ///     commit-date-relative churn would leak (the changed files are the most recently changed by
    ///     construction), so the benchmark cannot validate it and it stays off by default.
    /// </summary>
    public double GitChurnWeight { get; init; }

    /// <summary>
    ///     Whether a file whose reduced content is still very large is replaced with a compact structural sketch
    ///     (its type and member names, no bodies) so it keeps presence and navigation without consuming the
    ///     budget that several smaller files need. Off by default; the corpus failure mode is multi-file
    ///     truncation rather than single-giant-file pack-outs, so this is opt-in and does not change the default
    ///     output. Overridden by <c>FUSE_SKETCH_HUGE</c> (<c>1</c>, <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool SketchHugeFiles { get; init; }

    /// <summary>
    ///     Whether the packer downgrades before it drops (P1): when the full reduced set would exceed the token
    ///     budget, the lower-relevance tail that would otherwise be cut is replaced with a compact structural
    ///     sketch instead, so a would-be-dropped file stays present as a navigable outline. Recall counts file
    ///     presence, so this targets the multi-file-truncation failure mode directly. Applies to query and focus
    ///     under a token budget. On by default: an A/B over the pinned corpus lifted query recall at the tight
    ///     budgets (10,000 token 39 to 46 percent, 25,000 50 to 54) with no per-repo regression at any budget
    ///     and the 50,000 headline unchanged, because it only adds sketched files and never displaces a fuller
    ///     one. Set <c>FUSE_DOWNGRADE_DROP=0</c> (or <c>off</c>/<c>false</c>) to reproduce the drop-only packer.
    /// </summary>
    public bool DowngradeBeforeDrop { get; init; } = true;

    /// <summary>
    ///     Whether the query path expands a query identifier with its corpus-statistically-associated identifiers
    ///     from a distributional thesaurus (pointwise mutual information of declared symbols co-occurring in the
    ///     same file), so a query term bridges to a related-but-different vocabulary the pseudo-relevance feedback
    ///     set never contained. Fully lexical, no model. Off pending an A/B. Overridden by <c>FUSE_THESAURUS</c>
    ///     (<c>1</c>, <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool DistributionalThesaurus { get; init; }

    /// <summary>
    ///     Whether expansion follows low-weight structural proximity edges in addition to type-reference edges
    ///     (item 7): a file is linked to its test or implementation counterpart and same-stem siblings by path,
    ///     so expansion reaches a related file the type-reference graph misses when references are incomplete.
    ///     Proximity neighbours enter at a decayed weight, below a real reference. Off pending an A/B. Overridden
    ///     by <c>FUSE_PROXIMITY</c> (<c>1</c>, <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool ProximityEdges { get; init; }

    /// <summary>
    ///     Whether the query path adds a member-level retrieval pass (Q5): each declared member is indexed as
    ///     its own document and the per-member scores are rolled up to a file score (best member wins), then
    ///     merged with the file-granular ranking without dropping a seed. This surfaces a file whose match is
    ///     concentrated in one member of an otherwise large file, which whole-file length normalization dilutes.
    ///     Off pending an A/B. Overridden by <c>FUSE_MEMBER_LEVEL</c> (<c>1</c>, <c>on</c>, or <c>true</c>).
    /// </summary>
    public bool MemberLevelRetrieval { get; init; }

    /// <summary>
    ///     Whether expansion follows coarse project-reference edges (item 8): each candidate is linked to the
    ///     files in the projects its owning <c>.csproj</c> references or is referenced by, so a seed reaches a
    ///     related file across an assembly boundary that the intra-project type-reference graph misses (for
    ///     example a changed library file and an integration test in the test project that does not name its
    ///     type). Cross-project neighbours enter at the same decayed proximity weight, below a real reference.
    ///     Off pending an A/B. Overridden by <c>FUSE_PROJECT_GRAPH</c> (<c>1</c>, <c>on</c>, or <c>true</c>).
    /// </summary>
    public bool ProjectGraph { get; init; }

    /// <summary>
    ///     Returns a copy of <paramref name="configured" /> (or the defaults when <c>null</c>) with any
    ///     environment-variable overrides applied. The environment is consulted only here, so the resolved
    ///     record is the single source of truth for the run.
    /// </summary>
    /// <param name="configured">The request's configured options, or <c>null</c> to start from the defaults.</param>
    /// <returns>The resolved options with <c>FUSE_CENTRALITY_WEIGHT</c> and <c>FUSE_QUERY_EXPANSION</c> applied.</returns>
    public static ExperimentalOptions ResolveFromEnvironment(ExperimentalOptions? configured = null)
    {
        var resolved = configured ?? new ExperimentalOptions();

        var centrality = resolved.CentralityWeight;
        var rawWeight = Environment.GetEnvironmentVariable("FUSE_CENTRALITY_WEIGHT");
        if (!string.IsNullOrWhiteSpace(rawWeight) &&
            double.TryParse(rawWeight, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) &&
            weight >= 0)
        {
            centrality = weight;
        }

        var queryExpansion = resolved.QueryExpansion;
        var rawExpansion = Environment.GetEnvironmentVariable("FUSE_QUERY_EXPANSION");
        if (!string.IsNullOrWhiteSpace(rawExpansion) &&
            (rawExpansion.Equals("0", StringComparison.Ordinal) ||
             rawExpansion.Equals("off", StringComparison.OrdinalIgnoreCase) ||
             rawExpansion.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            queryExpansion = false;
        }

        var tieredEmission = resolved.TieredEmission;
        var rawTiered = Environment.GetEnvironmentVariable("FUSE_TIERED_EMISSION");
        if (!string.IsNullOrWhiteSpace(rawTiered))
        {
            if (rawTiered.Equals("1", StringComparison.Ordinal) ||
                rawTiered.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                rawTiered.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                tieredEmission = true;
            }
            else if (rawTiered.Equals("0", StringComparison.Ordinal) ||
                     rawTiered.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                     rawTiered.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                tieredEmission = false;
            }
        }

        var multiQueryFusion = resolved.MultiQueryFusion;
        var rawFusion = Environment.GetEnvironmentVariable("FUSE_QUERY_FUSION");
        if (!string.IsNullOrWhiteSpace(rawFusion) &&
            (rawFusion.Equals("1", StringComparison.Ordinal) ||
             rawFusion.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawFusion.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            multiQueryFusion = true;
        }

        var budgetAwareExpansion = resolved.BudgetAwareExpansion;
        var rawBudget = Environment.GetEnvironmentVariable("FUSE_BUDGET_EXPANSION");
        if (!string.IsNullOrWhiteSpace(rawBudget) &&
            (rawBudget.Equals("0", StringComparison.Ordinal) ||
             rawBudget.Equals("off", StringComparison.OrdinalIgnoreCase) ||
             rawBudget.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            budgetAwareExpansion = false;
        }

        var denseRerank = resolved.DenseRerank;
        var rawRerank = Environment.GetEnvironmentVariable("FUSE_RERANK");
        if (!string.IsNullOrWhiteSpace(rawRerank) &&
            (rawRerank.Equals("1", StringComparison.Ordinal) ||
             rawRerank.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawRerank.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            denseRerank = true;
        }

        var gitChurnWeight = resolved.GitChurnWeight;
        var rawChurn = Environment.GetEnvironmentVariable("FUSE_GIT_CHURN_WEIGHT");
        if (!string.IsNullOrWhiteSpace(rawChurn) &&
            double.TryParse(rawChurn, NumberStyles.Float, CultureInfo.InvariantCulture, out var churnWeight) &&
            churnWeight >= 0)
        {
            gitChurnWeight = churnWeight;
        }

        var sketchHugeFiles = resolved.SketchHugeFiles;
        var rawSketch = Environment.GetEnvironmentVariable("FUSE_SKETCH_HUGE");
        if (!string.IsNullOrWhiteSpace(rawSketch) &&
            (rawSketch.Equals("1", StringComparison.Ordinal) ||
             rawSketch.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawSketch.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            sketchHugeFiles = true;
        }

        var downgradeBeforeDrop = resolved.DowngradeBeforeDrop;
        var rawDowngrade = Environment.GetEnvironmentVariable("FUSE_DOWNGRADE_DROP");
        if (!string.IsNullOrWhiteSpace(rawDowngrade) &&
            (rawDowngrade.Equals("0", StringComparison.Ordinal) ||
             rawDowngrade.Equals("off", StringComparison.OrdinalIgnoreCase) ||
             rawDowngrade.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            downgradeBeforeDrop = false;
        }

        var distributionalThesaurus = resolved.DistributionalThesaurus;
        var rawThesaurus = Environment.GetEnvironmentVariable("FUSE_THESAURUS");
        if (!string.IsNullOrWhiteSpace(rawThesaurus) &&
            (rawThesaurus.Equals("1", StringComparison.Ordinal) ||
             rawThesaurus.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawThesaurus.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            distributionalThesaurus = true;
        }

        var proximityEdges = resolved.ProximityEdges;
        var rawProximity = Environment.GetEnvironmentVariable("FUSE_PROXIMITY");
        if (!string.IsNullOrWhiteSpace(rawProximity) &&
            (rawProximity.Equals("1", StringComparison.Ordinal) ||
             rawProximity.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawProximity.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            proximityEdges = true;
        }

        var memberLevelRetrieval = resolved.MemberLevelRetrieval;
        var rawMember = Environment.GetEnvironmentVariable("FUSE_MEMBER_LEVEL");
        if (!string.IsNullOrWhiteSpace(rawMember) &&
            (rawMember.Equals("1", StringComparison.Ordinal) ||
             rawMember.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawMember.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            memberLevelRetrieval = true;
        }

        var projectGraph = resolved.ProjectGraph;
        var rawProjectGraph = Environment.GetEnvironmentVariable("FUSE_PROJECT_GRAPH");
        if (!string.IsNullOrWhiteSpace(rawProjectGraph) &&
            (rawProjectGraph.Equals("1", StringComparison.Ordinal) ||
             rawProjectGraph.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawProjectGraph.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            projectGraph = true;
        }

        return resolved with
        {
            CentralityWeight = centrality,
            QueryExpansion = queryExpansion,
            TieredEmission = tieredEmission,
            MultiQueryFusion = multiQueryFusion,
            BudgetAwareExpansion = budgetAwareExpansion,
            DenseRerank = denseRerank,
            GitChurnWeight = gitChurnWeight,
            SketchHugeFiles = sketchHugeFiles,
            DowngradeBeforeDrop = downgradeBeforeDrop,
            DistributionalThesaurus = distributionalThesaurus,
            ProximityEdges = proximityEdges,
            MemberLevelRetrieval = memberLevelRetrieval,
            ProjectGraph = projectGraph,
        };
    }
}
