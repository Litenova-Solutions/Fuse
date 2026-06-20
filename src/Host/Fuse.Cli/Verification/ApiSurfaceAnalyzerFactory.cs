namespace Fuse.Cli.Verification;

/// <summary>
///     Selects the API-surface analyzer available in the current build: the Roslyn analyzer for the
///     framework-dependent tool, or the regex analyzer for Native AOT.
/// </summary>
public static class ApiSurfaceAnalyzerFactory
{
    /// <summary>
    ///     Creates the most accurate analyzer available in this build.
    /// </summary>
    /// <returns>
    ///     A Roslyn-backed analyzer when compiled with Roslyn support; otherwise the AOT-clean regex analyzer.
    /// </returns>
    public static IApiSurfaceAnalyzer Create() =>
#if FUSE_ROSLYN
        new RoslynApiSurfaceAnalyzer();
#else
        new RegexApiSurfaceAnalyzer();
#endif

    /// <summary>
    ///     Gets the name of the analysis backend in use, for reporting.
    /// </summary>
    public static string BackendName =>
#if FUSE_ROSLYN
        "roslyn";
#else
        "regex";
#endif
}
