using System.Text;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Languages.CSharp.Maps;

/// <summary>
///     Builds an HTTP route map from ASP.NET controller actions (<c>[Route]</c>, <c>[HttpGet]</c>, and related
///     attributes) and minimal-API registrations (<c>MapGet</c>, <c>MapPost</c>, and so on) in <c>.cs</c> files.
/// </summary>
/// <remarks>
///     Routes are recovered by regex rather than by compiling the project, so the table is best-effort:
///     attribute routes built from constants or interpolation are not resolved, verb-only actions fall back to
///     the handler name as the path, and conventional (attribute-less) routing is not detected. Rows are
///     emitted in case-insensitive order inside a <c>&lt;!-- fuse:route-map --&gt;</c> comment block.
/// </remarks>
public sealed class CSharpRouteMapGenerator : Fuse.Plugins.Abstractions.Maps.IRouteMapGenerator
{
    private static readonly Regex ControllerRouteRegex = new(
        @"\[Route\(""?(?<route>[^""\)]+)""?\)\]\s*(?:public\s+)?class\s+(?<controller>\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ControllerActionWithRouteRegex = new(
        @"\[(?:Http(?<verb>Get|Post|Put|Delete|Patch)|Route)\(""?(?<route>[^""\)]+)""?\)\][^{;]*?(?:public\s+)?(?:async\s+)?[\w<>\[\],\s]+\s+(?<handler>\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ControllerActionVerbOnlyRegex = new(
        @"\[Http(?<verb>Get|Post|Put|Delete|Patch)\][^{;]*?(?:public\s+)?(?:async\s+)?[\w<>\[\],\s]+\s+(?<handler>\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex MinimalApiRegex = new(
        @"\.Map(?:Get|Post|Put|Delete|Patch)\s*\(\s*""(?<route>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public string Generate(IReadOnlyDictionary<string, string> fileContents)
    {
        var rows = new List<string>();

        foreach (var (path, content) in fileContents)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var controllerRoute = string.Empty;
            foreach (Match match in ControllerRouteRegex.Matches(content))
            {
                controllerRoute = match.Groups["route"].Value.TrimEnd('/');
            }

            foreach (Match match in ControllerActionWithRouteRegex.Matches(content))
            {
                var verb = match.Groups["verb"].Success
                    ? match.Groups["verb"].Value.ToUpperInvariant()
                    : "GET";
                var route = CombineRoutes(controllerRoute, match.Groups["route"].Value);
                var handler = match.Groups["handler"].Value;
                rows.Add($"{verb,-6} {route,-40} {handler} ({path})");
            }

            foreach (Match match in ControllerActionVerbOnlyRegex.Matches(content))
            {
                var verb = match.Groups["verb"].Value.ToUpperInvariant();
                var handler = match.Groups["handler"].Value;
                var route = CombineRoutes(controllerRoute, handler);
                rows.Add($"{verb,-6} {route,-40} {handler} ({path})");
            }

            foreach (Match match in MinimalApiRegex.Matches(content))
            {
                var route = match.Groups["route"].Value;
                var verb = ExtractMinimalApiVerb(match.Value);
                rows.Add($"{verb,-6} {route,-40} minimal-api ({path})");
            }
        }

        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<!-- fuse:route-map");
        sb.AppendLine("VERB   PATH                                     HANDLER");
        foreach (var row in rows.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(row);
        sb.AppendLine("-->");
        return sb.ToString();
    }

    private static string ExtractMinimalApiVerb(string matchValue)
    {
        foreach (var verb in new[] { "Get", "Post", "Put", "Delete", "Patch" })
        {
            if (matchValue.Contains($"Map{verb}", StringComparison.OrdinalIgnoreCase))
                return verb.ToUpperInvariant();
        }

        return "GET";
    }

    private static string CombineRoutes(string prefix, string actionRoute)
    {
        if (string.IsNullOrEmpty(prefix))
            return actionRoute;

        if (string.IsNullOrEmpty(actionRoute))
            return prefix;

        return $"{prefix.TrimEnd('/')}/{actionRoute.TrimStart('/')}";
    }
}
