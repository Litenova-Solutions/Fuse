using System.Globalization;

namespace Fuse.Fusion;

/// <summary>
///     Tunable knobs for focus and change scoping, tiered emission, and post-reduction packing whose defaults
///     may change between releases. Carried explicitly on a <see cref="FusionRequest" /> so a run's
///     configuration is part of the request rather than ambient process state, and recorded in the
///     machine-readable run report so a committed measurement is self-describing.
/// </summary>
/// <remarks>
///     Open-ended task localization (query expansion, hop decay, member-level retrieval, fielded comments, and
///     related lexical ranking knobs) lives in <c>Fuse.Retrieval</c>, not here. This record covers only what
///     the fusion pipeline consumes for focus/change scoping and emission shaping.
///     Environment variables override the configured values (see <see cref="ResolveFromEnvironment" />) so an
///     operator can A/B a knob without rebuilding, but the override is applied on top of the request's values
///     rather than read from ambient state at the point of use. This keeps a result reproducible: the resolved
///     options describe exactly what produced it.
/// </remarks>
public sealed record ExperimentalOptions
{
    /// <summary>
    ///     The query-independent graph-centrality weight blended into focus seed and expansion scores, so at
    ///     equal relevance the more depended-upon file ranks earlier. <c>0</c> disables the prior. Overridden
    ///     by <c>FUSE_CENTRALITY_WEIGHT</c>.
    /// </summary>
    public double CentralityWeight { get; init; } = 0.15;

    /// <summary>
    ///     Whether tiered emission is on for focus scoping: dependency-expanded neighbour files (provenance hop
    ///     two or deeper) are reduced to signature skeletons rather than full bodies, so each costs fewer tokens
    ///     and the budget-aware packer fits more files. Seeds keep their planned tier. Recall counts file
    ///     presence, so fitting more truth files raises recall, most on large change sets. On by default: an A/B
    ///     over the pinned corpus lifted query recall at 50k from 51 to 55 percent and focus from 71 to 77
    ///     percent at fewer tokens, with no per-repo regression. Set <c>FUSE_TIERED_EMISSION=0</c> (or
    ///     <c>off</c>/<c>false</c>) to reproduce the untiered ordering.
    /// </summary>
    public bool TieredEmission { get; init; } = true;

    /// <summary>
    ///     Whether a file whose reduced content is still very large is replaced with a compact structural sketch
    ///     (its type and member names, no bodies) so it keeps presence and navigation without consuming the
    ///     budget that several smaller files need. Off by default; the corpus failure mode is multi-file
    ///     truncation rather than single-giant-file pack-outs, so this is opt-in and does not change the default
    ///     output. Overridden by <c>FUSE_SKETCH_HUGE</c> (<c>1</c>, <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool SketchHugeFiles { get; init; }

    /// <summary>
    ///     Whether the packer downgrades before it drops: when the full reduced set would exceed the token
    ///     budget, the lower-relevance tail that would otherwise be cut is replaced with a compact structural
    ///     sketch instead, so a would-be-dropped file stays present as a navigable outline. Recall counts file
    ///     presence, so this targets the multi-file-truncation failure mode directly. Applies to focus under a
    ///     token budget. On by default: an A/B over the pinned corpus lifted query recall at the tight budgets
    ///     (10,000 token 39 to 46 percent, 25,000 50 to 54) with no per-repo regression at any budget and the
    ///     50,000 headline unchanged, because it only adds sketched files and never displaces a fuller one. Set
    ///     <c>FUSE_DOWNGRADE_DROP=0</c> (or <c>off</c>/<c>false</c>) to reproduce the drop-only packer.
    /// </summary>
    public bool DowngradeBeforeDrop { get; init; } = true;

    /// <summary>
    ///     Whether focus expansion follows low-weight structural proximity edges in addition to type-reference
    ///     edges: a file is linked to its test or implementation counterpart and same-stem siblings by path, so
    ///     expansion reaches a related file the type-reference graph misses when references are incomplete.
    ///     Proximity neighbours enter at a decayed weight, below a real reference. Off pending an A/B.
    ///     Overridden by <c>FUSE_PROXIMITY</c> (<c>1</c>, <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool ProximityEdges { get; init; }

    /// <summary>
    ///     Whether focus expansion follows coarse project-reference edges: each candidate is linked to the files
    ///     in the projects its owning <c>.csproj</c> references or is referenced by, so a seed reaches a related
    ///     file across an assembly boundary that the intra-project type-reference graph misses. Cross-project
    ///     neighbours enter at the same decayed proximity weight, below a real reference. Off pending an A/B.
    ///     Overridden by <c>FUSE_PROJECT_GRAPH</c> (<c>1</c>, <c>on</c>, or <c>true</c> enables it).
    /// </summary>
    public bool ProjectGraph { get; init; }

    /// <summary>
    ///     Returns a copy of <paramref name="configured" /> (or the defaults when <c>null</c>) with any
    ///     environment-variable overrides applied. The environment is consulted only here, so the resolved
    ///     record is the single source of truth for the run.
    /// </summary>
    /// <param name="configured">The request's configured options, or <c>null</c> to start from the defaults.</param>
    /// <returns>The resolved options with every recognized <c>FUSE_*</c> override applied on top of the configured values.</returns>
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

        var proximityEdges = resolved.ProximityEdges;
        var rawProximity = Environment.GetEnvironmentVariable("FUSE_PROXIMITY");
        if (!string.IsNullOrWhiteSpace(rawProximity) &&
            (rawProximity.Equals("1", StringComparison.Ordinal) ||
             rawProximity.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             rawProximity.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            proximityEdges = true;
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
            TieredEmission = tieredEmission,
            SketchHugeFiles = sketchHugeFiles,
            DowngradeBeforeDrop = downgradeBeforeDrop,
            ProximityEdges = proximityEdges,
            ProjectGraph = projectGraph,
        };
    }
}
