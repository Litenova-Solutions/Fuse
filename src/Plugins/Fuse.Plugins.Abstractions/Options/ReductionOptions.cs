namespace Fuse.Plugins.Abstractions.Options;

/// <summary>
///     Configuration options for the content reduction pipeline.
/// </summary>
/// <remarks>
///     The single <see cref="Level" /> dial controls C# structural reduction intensity; the per-transform
///     decisions (comment, using, namespace, and region removal, aggressive compression, skeleton and
///     public-API extraction) are derived from it. The remaining flags are orthogonal to that scale and
///     control whitespace normalization, minification for HTML, Razor, and XML, redaction, and the optional
///     prepended sections (route map, project graph, pattern summary, semantic markers).
/// </remarks>
public sealed record ReductionOptions
{
    /// <summary>
    ///     The C# structural reduction level. Drives the derived C# transform flags.
    /// </summary>
    public ReductionLevel Level { get; init; }

    /// <summary>
    ///     Gets a value indicating whether leading and trailing whitespace is trimmed from each line.
    /// </summary>
    public bool TrimContent { get; init; }

    /// <summary>
    ///     Gets a value indicating whether blank lines are collapsed.
    /// </summary>
    public bool UseCondensing { get; init; }

    /// <summary>
    ///     Gets a value indicating whether XML-based files are minified.
    /// </summary>
    public bool MinifyXmlFiles { get; init; }

    /// <summary>
    ///     Gets a value indicating whether HTML and Razor view files are minified.
    /// </summary>
    /// <remarks>
    ///     <c>.razor</c> files are always minified regardless of this setting.
    ///     <c>.cshtml</c>, <c>.html</c>, and <c>.htm</c> files respect this flag.
    /// </remarks>
    public bool MinifyHtmlAndRazor { get; init; }

    /// <summary>
    ///     Gets a value indicating whether structural annotation comments are prepended to file entries.
    /// </summary>
    public bool IncludeSemanticMarkers { get; init; }

    /// <summary>
    ///     Gets a value indicating whether a cross-codebase pattern summary is detected and appended.
    /// </summary>
    public bool IncludePatternSummary { get; init; }

    /// <summary>
    ///     Gets a value indicating whether secrets are redacted before token counting.
    /// </summary>
    public bool EnableRedaction { get; init; }

    /// <summary>
    ///     Gets a value indicating whether a redaction count summary is appended to output.
    /// </summary>
    public bool IncludeRedactReport { get; init; }

    /// <summary>
    ///     Gets a value indicating whether an ASP.NET route map is prepended to output.
    /// </summary>
    public bool IncludeRouteMap { get; init; }

    /// <summary>
    ///     Gets a value indicating whether a solution/project graph is prepended to output.
    /// </summary>
    public bool IncludeProjectGraph { get; init; }

    /// <summary>
    ///     Gets a value indicating whether EF Core migrations, model snapshots, and other machine-generated C#
    ///     are collapsed to their signatures, dropping the large auto-generated method bodies.
    /// </summary>
    public bool CollapseGeneratedCode { get; init; }

    /// <summary>C# comments are removed at <see cref="ReductionLevel.Standard" /> and <see cref="ReductionLevel.Aggressive" />.</summary>
    public bool RemoveCSharpComments => Level is ReductionLevel.Standard or ReductionLevel.Aggressive;

    /// <summary>C# <c>using</c> directives are removed at <see cref="ReductionLevel.Standard" /> and <see cref="ReductionLevel.Aggressive" />.</summary>
    public bool RemoveCSharpUsings => Level is ReductionLevel.Standard or ReductionLevel.Aggressive;

    /// <summary>C# namespace declarations are removed at <see cref="ReductionLevel.Standard" /> and <see cref="ReductionLevel.Aggressive" />.</summary>
    public bool RemoveCSharpNamespaces => Level is ReductionLevel.Standard or ReductionLevel.Aggressive;

    /// <summary>C# <c>#region</c> directives are removed at <see cref="ReductionLevel.Standard" /> and <see cref="ReductionLevel.Aggressive" />.</summary>
    public bool RemoveCSharpRegions => Level is ReductionLevel.Standard or ReductionLevel.Aggressive;

    /// <summary>Aggressive whitespace and syntax compression runs only at <see cref="ReductionLevel.Aggressive" />.</summary>
    public bool AggressiveCSharpReduction => Level is ReductionLevel.Aggressive;

    /// <summary>Skeleton extraction runs at <see cref="ReductionLevel.Skeleton" /> and <see cref="ReductionLevel.PublicApi" />.</summary>
    public bool SkeletonMode => Level is ReductionLevel.Skeleton or ReductionLevel.PublicApi;

    /// <summary>Skeleton extraction is restricted to public and protected members only at <see cref="ReductionLevel.PublicApi" />.</summary>
    public bool PublicApiMode => Level is ReductionLevel.PublicApi;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReductionOptions" /> record.
    /// </summary>
    /// <param name="level">The C# structural reduction level. Defaults to <see cref="ReductionLevel.None" />.</param>
    /// <param name="trimContent">Whether per-line leading and trailing whitespace is trimmed.</param>
    /// <param name="useCondensing">Whether blank lines are collapsed.</param>
    /// <param name="minifyXmlFiles">Whether XML-based files are minified.</param>
    /// <param name="minifyHtmlAndRazor">Whether HTML and Razor view files are minified.</param>
    /// <param name="includeSemanticMarkers">Whether structural annotation comments are prepended.</param>
    /// <param name="includePatternSummary">Whether a cross-codebase pattern summary is appended.</param>
    /// <param name="enableRedaction">Whether secrets are redacted before token counting.</param>
    /// <param name="includeRedactReport">Whether a redaction count summary is appended.</param>
    /// <param name="includeRouteMap">Whether an ASP.NET route map is prepended.</param>
    /// <param name="includeProjectGraph">Whether a solution/project graph is prepended.</param>
    /// <param name="collapseGeneratedCode">Whether machine-generated C# bodies are collapsed to signatures.</param>
    public ReductionOptions(
        ReductionLevel level = ReductionLevel.None,
        bool trimContent = true,
        bool useCondensing = true,
        bool minifyXmlFiles = true,
        bool minifyHtmlAndRazor = true,
        bool includeSemanticMarkers = false,
        bool includePatternSummary = false,
        bool enableRedaction = true,
        bool includeRedactReport = false,
        bool includeRouteMap = false,
        bool includeProjectGraph = false,
        bool collapseGeneratedCode = false)
    {
        Level = level;
        TrimContent = trimContent;
        UseCondensing = useCondensing;
        MinifyXmlFiles = minifyXmlFiles;
        MinifyHtmlAndRazor = minifyHtmlAndRazor;
        IncludeSemanticMarkers = includeSemanticMarkers;
        IncludePatternSummary = includePatternSummary;
        EnableRedaction = enableRedaction;
        IncludeRedactReport = includeRedactReport;
        IncludeRouteMap = includeRouteMap;
        IncludeProjectGraph = includeProjectGraph;
        CollapseGeneratedCode = collapseGeneratedCode;
    }
}
