namespace Fuse.Emission.Serialization;

/// <summary>
///     The resolved experimental scoring knobs recorded in a run report, so a committed measurement names the
///     configuration that produced it rather than depending on invisible environment state.
/// </summary>
public sealed class JsonExperimentalOptionsDto
{
    /// <summary>
    ///     The resolved graph-centrality weight blended into seed and expansion scores.
    /// </summary>
    public double CentralityWeight { get; set; }

    /// <summary>
    ///     The resolved per-hop score decay applied to dependency-expanded neighbours (item 5 tuning scalar).
    /// </summary>
    public double HopDecay { get; set; }

    /// <summary>
    ///     The resolved per-term weight of pseudo-relevance-feedback expansion terms (item 5 tuning scalar).
    /// </summary>
    public double ExpansionWeight { get; set; }

    /// <summary>
    ///     Whether pseudo-relevance feedback query expansion ran on the query path.
    /// </summary>
    public bool QueryExpansion { get; set; }

    /// <summary>
    ///     Whether tiered emission skeletonized dependency-expanded neighbours on the query and focus paths.
    /// </summary>
    public bool TieredEmission { get; set; }

    /// <summary>
    ///     Whether the query path fused several query variants with Reciprocal Rank Fusion.
    /// </summary>
    public bool MultiQueryFusion { get; set; }

    /// <summary>
    ///     Whether query-path dependency expansion was budget-aware (neighbours admitted by estimated cost
    ///     against the token ceiling rather than admitting the whole neighbourhood for the packer to cut).
    /// </summary>
    public bool BudgetAwareExpansion { get; set; }

    /// <summary>
    ///     The weight of the git churn ranking prior on the query path (<c>0</c> when off).
    /// </summary>
    public double GitChurnWeight { get; set; }

    /// <summary>
    ///     Whether over-large reduced files were replaced with a structural sketch (item 16).
    /// </summary>
    public bool SketchHugeFiles { get; set; }

    /// <summary>
    ///     Whether the lower-relevance tail that would exceed the budget was downgraded to a sketch instead of
    ///     dropped.
    /// </summary>
    public bool DowngradeBeforeDrop { get; set; }

    /// <summary>
    ///     Whether the query was expanded with a local distributional thesaurus of co-occurring identifiers (Q4).
    /// </summary>
    public bool DistributionalThesaurus { get; set; }

    /// <summary>
    ///     Whether expansion followed low-weight structural proximity edges in addition to type references (item 7).
    /// </summary>
    public bool ProximityEdges { get; set; }

    /// <summary>
    ///     Whether the query path added a member-level retrieval pass rolled up to file scores (Q5).
    /// </summary>
    public bool MemberLevelRetrieval { get; set; }
}
