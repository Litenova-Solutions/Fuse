using System.Text.RegularExpressions;

namespace Fuse.Plugins.Abstractions.Maps;

/// <summary>
///     Extracts <c>ProjectReference</c> include paths from raw <c>.csproj</c> text.
/// </summary>
/// <remarks>
///     References resolved through imported targets, MSBuild properties, or globs are not recovered; only
///     literal <c>&lt;ProjectReference Include="..."&gt;</c> elements are matched.
/// </remarks>
public static class CsprojProjectReferenceParser
{
    private static readonly Regex ProjectReferenceRegex = new(
        @"<ProjectReference\s+Include=""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    ///     Returns the include path of every <c>ProjectReference</c> element in the supplied content.
    /// </summary>
    /// <param name="csprojContent">The raw <c>.csproj</c> file text.</param>
    /// <returns>Normalized forward-slash include paths, in document order.</returns>
    public static IEnumerable<string> EnumerateIncludePaths(string csprojContent)
    {
        foreach (Match match in ProjectReferenceRegex.Matches(csprojContent))
            yield return match.Groups["path"].Value.Replace('\\', '/');
    }
}
