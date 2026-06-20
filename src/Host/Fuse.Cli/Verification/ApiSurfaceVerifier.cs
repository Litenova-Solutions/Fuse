using System.Text.RegularExpressions;

namespace Fuse.Cli.Verification;

/// <summary>
///     Measures how much of a source set's public API surface survives in fused output.
/// </summary>
/// <remarks>
///     The source side is parsed by an <see cref="IApiSurfaceAnalyzer" />; the fused side is matched by text
///     presence (declared type names, call or declaration targets, and literal route templates), because
///     reduced or skeleton output is not always valid C#. This matching is AOT-clean regex, mirroring the
///     benchmark fidelity oracle so the numbers are comparable.
/// </remarks>
public sealed partial class ApiSurfaceVerifier
{
    private readonly IApiSurfaceAnalyzer _analyzer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ApiSurfaceVerifier" /> class.
    /// </summary>
    /// <param name="analyzer">The analyzer used to extract the source-side API surface.</param>
    public ApiSurfaceVerifier(IApiSurfaceAnalyzer analyzer) => _analyzer = analyzer;

    /// <summary>
    ///     Verifies preservation of the public API across the supplied source contents.
    /// </summary>
    /// <param name="sourceContents">The source text of each included file.</param>
    /// <param name="fusedOutput">The fused output text to check against.</param>
    /// <returns>A report with per-category totals, preserved counts, and ratios.</returns>
    public ApiSurfaceReport Verify(IEnumerable<string> sourceContents, string fusedOutput)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var methods = new HashSet<string>(StringComparer.Ordinal);
        var routes = new HashSet<string>(StringComparer.Ordinal);

        var fileCount = 0;
        foreach (var source in sourceContents)
        {
            fileCount++;
            _analyzer.Collect(source, types, methods, routes);
        }

        var fusedTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FusedTypeRegex().Matches(fusedOutput))
            fusedTypeNames.Add(m.Groups[1].Value);

        var fusedCallNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FusedCallRegex().Matches(fusedOutput))
            fusedCallNames.Add(m.Groups[1].Value);

        var preservedTypes = types.Count(fusedTypeNames.Contains);
        var preservedMethods = methods.Count(fusedCallNames.Contains);
        var preservedRoutes = routes.Count(r => fusedOutput.Contains(r, StringComparison.Ordinal));

        return new ApiSurfaceReport(
            fileCount,
            new ApiCategoryResult(types.Count, preservedTypes),
            new ApiCategoryResult(methods.Count, preservedMethods),
            new ApiCategoryResult(routes.Count, preservedRoutes));
    }

    [GeneratedRegex(@"\b(?:class|interface|struct|record|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex FusedTypeRegex();

    [GeneratedRegex(@"(?<![.\w])([A-Za-z_]\w*)\s*(?:<[^>\n]*>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex FusedCallRegex();
}

/// <summary>
///     Per-category preservation result.
/// </summary>
/// <param name="Total">The number of symbols found in the source.</param>
/// <param name="Preserved">The number of those symbols present in the fused output.</param>
public sealed record ApiCategoryResult(int Total, int Preserved)
{
    /// <summary>
    ///     The preserved fraction, or <c>1.0</c> when there are no symbols in the category.
    /// </summary>
    public double Ratio => Total == 0 ? 1.0 : (double)Preserved / Total;
}

/// <summary>
///     The outcome of an API-surface verification.
/// </summary>
/// <param name="FileCount">The number of source files analyzed.</param>
/// <param name="Types">Type preservation.</param>
/// <param name="Methods">Method preservation.</param>
/// <param name="Routes">Route preservation.</param>
public sealed record ApiSurfaceReport(
    int FileCount,
    ApiCategoryResult Types,
    ApiCategoryResult Methods,
    ApiCategoryResult Routes);
