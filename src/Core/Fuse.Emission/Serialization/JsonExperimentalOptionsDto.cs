namespace Fuse.Emission.Serialization;

/// <summary>
///     The resolved experimental knobs recorded in a run report, so a committed measurement names the
///     configuration that produced it rather than depending on invisible environment state.
/// </summary>
/// <remarks>
///     Query-path retrieval knobs (open-ended localize, query expansion, hop decay, and related lexical ranking
///     levers) are recorded by the retrieval layer, not here. This DTO covers only focus/change scoping and
///     emission shaping options consumed by the fusion pipeline.
/// </remarks>
public sealed class JsonExperimentalOptionsDto
{
    /// <summary>
    ///     The resolved graph-centrality weight blended into focus seed and expansion scores.
    /// </summary>
    public double CentralityWeight { get; set; }

    /// <summary>
    ///     Whether tiered emission skeletonized dependency-expanded neighbours on the focus path.
    /// </summary>
    public bool TieredEmission { get; set; }

    /// <summary>
    ///     Whether over-large reduced files were replaced with a structural sketch.
    /// </summary>
    public bool SketchHugeFiles { get; set; }

    /// <summary>
    ///     Whether the lower-relevance tail that would exceed the budget was downgraded to a sketch instead of
    ///     dropped.
    /// </summary>
    public bool DowngradeBeforeDrop { get; set; }

    /// <summary>
    ///     Whether focus expansion followed low-weight structural proximity edges in addition to type references.
    /// </summary>
    public bool ProximityEdges { get; set; }

    /// <summary>
    ///     Whether focus expansion followed coarse project-reference edges across assembly boundaries.
    /// </summary>
    public bool ProjectGraph { get; set; }
}
