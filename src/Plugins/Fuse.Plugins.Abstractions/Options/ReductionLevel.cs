namespace Fuse.Plugins.Abstractions.Options;

/// <summary>
///     The single dial controlling how aggressively C# source is reduced. Each level expands to a fixed set
///     of C# transform decisions (see <see cref="ReductionOptions" />), replacing the former cluster of
///     overlapping per-transform booleans.
/// </summary>
/// <remarks>
///     <see cref="None" />, <see cref="Standard" />, and <see cref="Aggressive" /> form an increasing
///     text-reduction scale that preserves source shape. <see cref="Skeleton" /> and <see cref="PublicApi" />
///     are a different representation: structural signatures with bodies removed, which is why they are not a
///     simple continuation of the text scale. Orthogonal concerns (redaction, minification, route map, project
///     graph, semantic markers, generated-code collapse) remain separate flags on <see cref="ReductionOptions" />.
/// </remarks>
public enum ReductionLevel
{
    /// <summary>No C# structural reduction. Whitespace normalization and the orthogonal flags still apply.</summary>
    None,

    /// <summary>Removes C# comments, <c>using</c> directives, namespace wrappers, and <c>#region</c> directives.</summary>
    Standard,

    /// <summary>The <see cref="Standard" /> removals plus aggressive whitespace and syntax compression.</summary>
    Aggressive,

    /// <summary>Emits structural skeletons (type and member signatures) with bodies removed.</summary>
    Skeleton,

    /// <summary>Like <see cref="Skeleton" /> but keeps only public and protected members.</summary>
    PublicApi,
}
