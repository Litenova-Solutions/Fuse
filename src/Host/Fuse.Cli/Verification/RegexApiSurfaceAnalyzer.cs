using System.Text.RegularExpressions;
using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Cli.Verification;

/// <summary>
///     AOT-clean regex implementation of <see cref="IApiSurfaceAnalyzer" />. Used when the tool is published
///     with Native AOT and the Roslyn analyzer is excluded.
/// </summary>
/// <remarks>
///     Types and methods are matched on source with comments and string literals blanked (see
///     <see cref="CSharpSourceSanitizer" />); routes are matched on the raw source because route templates
///     live inside string literals. This is best-effort: a constructor-free method regex and attribute and
///     minimal-API route patterns approximate the public surface without a full parse.
/// </remarks>
public sealed partial class RegexApiSurfaceAnalyzer : IApiSurfaceAnalyzer
{
    /// <inheritdoc />
    public void Collect(string source, ISet<string> types, ISet<string> methods, ISet<string> routes)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        var sanitized = CSharpSourceSanitizer.Sanitize(source);

        foreach (Match match in PublicTypeRegex().Matches(sanitized))
            types.Add(match.Groups[1].Value);

        foreach (Match match in PublicMethodRegex().Matches(sanitized))
            methods.Add(match.Groups[1].Value);

        foreach (Match match in RouteAttributeRegex().Matches(source))
            AddRoute(routes, match.Groups[1].Value);

        foreach (Match match in MinimalApiRouteRegex().Matches(source))
            AddRoute(routes, match.Groups[1].Value);
    }

    private static void AddRoute(ISet<string> routes, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            routes.Add(value);
    }

    [GeneratedRegex(@"\b(?:public|protected)(?:\s+(?:partial|sealed|abstract|static|readonly|unsafe))*\s+(?:class|interface|record|struct|enum)\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex PublicTypeRegex();

    [GeneratedRegex(@"\b(?:public|protected)(?:\s+(?:static|virtual|override|sealed|abstract|async|new|unsafe|extern|partial))*\s+[\w<>\[\].,\?]+\s+(\w+)\s*(?:<[^>\n]*>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex PublicMethodRegex();

    [GeneratedRegex("\\[(?:Route|Http(?:Get|Post|Put|Delete|Patch|Head|Options))\\s*\\(\\s*@?\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex RouteAttributeRegex();

    [GeneratedRegex("\\bMap(?:Get|Post|Put|Delete|Patch)\\s*\\(\\s*@?\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex MinimalApiRouteRegex();
}
