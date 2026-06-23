using System.Text.Json;

namespace Fuse.Emission.Serialization;

/// <summary>
///     Serializes an API-surface verification result to JSON using the source-generated context.
/// </summary>
/// <remarks>
///     Accepts primitive counts rather than a domain type so that the CLI verification types need not be
///     referenced from this assembly.
/// </remarks>
public static class VerifyReportSerializer
{
    /// <summary>
    ///     Builds the verify report JSON.
    /// </summary>
    /// <param name="backend">The analysis backend name (<c>roslyn</c> or <c>regex</c>).</param>
    /// <param name="files">The number of source files analyzed.</param>
    /// <param name="typesTotal">Total public and protected types found in the source.</param>
    /// <param name="typesPreserved">Types present in the fused output.</param>
    /// <param name="methodsTotal">Total public and protected methods found in the source.</param>
    /// <param name="methodsPreserved">Methods present in the fused output.</param>
    /// <param name="routesTotal">Total route templates found in the source.</param>
    /// <param name="routesPreserved">Route templates present in the fused output.</param>
    /// <returns>The verify report as a JSON string.</returns>
    public static string ToJson(
        string backend,
        int files,
        int typesTotal,
        int typesPreserved,
        int methodsTotal,
        int methodsPreserved,
        int routesTotal,
        int routesPreserved)
    {
        var dto = new JsonVerifyReportDto
        {
            Backend = backend,
            Files = files,
            Types = Category(typesTotal, typesPreserved),
            Methods = Category(methodsTotal, methodsPreserved),
            Routes = Category(routesTotal, routesPreserved),
        };

        return JsonSerializer.Serialize(dto, FuseEmissionJsonContext.Default.JsonVerifyReportDto);
    }

    private static JsonVerifyCategoryDto Category(int total, int preserved) => new()
    {
        Total = total,
        Preserved = preserved,
        Ratio = total == 0 ? 1.0 : Math.Round((double)preserved / total, 4),
    };
}
