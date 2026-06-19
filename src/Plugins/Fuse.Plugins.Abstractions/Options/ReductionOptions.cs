namespace Fuse.Plugins.Abstractions.Options;

/// <summary>
///     Configuration options for the content reduction pipeline.
/// </summary>
/// <remarks>
///     Controls whitespace normalization, C# structural reduction, and optional minification
///     for HTML, Razor, and XML file types.
/// </remarks>
public sealed record ReductionOptions
{
    /// <summary>
    ///     Gets a value indicating whether leading and trailing whitespace is trimmed from each line.
    /// </summary>
    public bool TrimContent { get; init; }

    /// <summary>
    ///     Gets a value indicating whether blank lines are collapsed.
    /// </summary>
    public bool UseCondensing { get; init; }

    /// <summary>
    ///     Gets a value indicating whether C# comments are removed.
    /// </summary>
    public bool RemoveCSharpComments { get; init; }

    /// <summary>
    ///     Gets a value indicating whether C# using directives are removed.
    /// </summary>
    public bool RemoveCSharpUsings { get; init; }

    /// <summary>
    ///     Gets a value indicating whether C# namespace declarations are removed.
    /// </summary>
    public bool RemoveCSharpNamespaces { get; init; }

    /// <summary>
    ///     Gets a value indicating whether C# region directives are removed.
    /// </summary>
    public bool RemoveCSharpRegions { get; init; }

    /// <summary>
    ///     Gets a value indicating whether aggressive C# syntax compression is applied.
    /// </summary>
    public bool AggressiveCSharpReduction { get; init; }

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
    ///     Gets a value indicating whether C# files emit structural skeleton only.
    /// </summary>
    public bool SkeletonMode { get; init; }

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
    ///     Gets a value indicating whether skeleton extraction emits only public and protected members.
    /// </summary>
    public bool PublicApiMode { get; init; }

    /// <summary>
    ///     Gets a value indicating whether a solution/project graph is prepended to output.
    /// </summary>
    public bool IncludeProjectGraph { get; init; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReductionOptions" /> record.
    /// </summary>
    public ReductionOptions(
        bool trimContent = true,
        bool useCondensing = true,
        bool removeCSharpComments = false,
        bool removeCSharpUsings = false,
        bool removeCSharpNamespaces = false,
        bool removeCSharpRegions = false,
        bool aggressiveCSharpReduction = false,
        bool minifyXmlFiles = true,
        bool minifyHtmlAndRazor = true,
        bool skeletonMode = false,
        bool includeSemanticMarkers = false,
        bool includePatternSummary = false,
        bool enableRedaction = true,
        bool includeRedactReport = false,
        bool includeRouteMap = false,
        bool publicApiMode = false,
        bool includeProjectGraph = false)
    {
        TrimContent = trimContent;
        UseCondensing = useCondensing;
        RemoveCSharpComments = removeCSharpComments;
        RemoveCSharpUsings = removeCSharpUsings;
        RemoveCSharpNamespaces = removeCSharpNamespaces;
        RemoveCSharpRegions = removeCSharpRegions;
        AggressiveCSharpReduction = aggressiveCSharpReduction;
        MinifyXmlFiles = minifyXmlFiles;
        MinifyHtmlAndRazor = minifyHtmlAndRazor;
        SkeletonMode = skeletonMode;
        IncludeSemanticMarkers = includeSemanticMarkers;
        IncludePatternSummary = includePatternSummary;
        EnableRedaction = enableRedaction;
        IncludeRedactReport = includeRedactReport;
        IncludeRouteMap = includeRouteMap;
        PublicApiMode = publicApiMode;
        IncludeProjectGraph = includeProjectGraph;
    }
}
