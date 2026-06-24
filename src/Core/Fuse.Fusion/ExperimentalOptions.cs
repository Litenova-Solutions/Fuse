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

        return resolved with
        {
            CentralityWeight = centrality,
            QueryExpansion = queryExpansion,
            TieredEmission = tieredEmission,
            MultiQueryFusion = multiQueryFusion,
        };
    }
}
