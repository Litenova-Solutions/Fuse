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
}
