namespace Fuse.Reduction.Options;



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

    ///     Initializes a new instance of the <see cref="ReductionOptions" /> record.

    /// </summary>

    /// <param name="trimContent">Whether to trim leading and trailing whitespace from each line.</param>

    /// <param name="useCondensing">Whether to collapse blank lines.</param>

    /// <param name="removeCSharpComments">Whether to remove C# comments.</param>

    /// <param name="removeCSharpUsings">Whether to remove C# using directives.</param>

    /// <param name="removeCSharpNamespaces">Whether to remove C# namespace declarations.</param>

    /// <param name="removeCSharpRegions">Whether to remove C# region directives.</param>

    /// <param name="aggressiveCSharpReduction">Whether to apply aggressive C# compression.</param>

    /// <param name="minifyXmlFiles">Whether to minify XML-based files.</param>

    /// <param name="minifyHtmlAndRazor">Whether to minify HTML and Razor view files.</param>

    public ReductionOptions(

        bool trimContent = true,

        bool useCondensing = true,

        bool removeCSharpComments = false,

        bool removeCSharpUsings = false,

        bool removeCSharpNamespaces = false,

        bool removeCSharpRegions = false,

        bool aggressiveCSharpReduction = false,

        bool minifyXmlFiles = true,

        bool minifyHtmlAndRazor = true)

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

    }

}

