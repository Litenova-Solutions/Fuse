using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Markers;

namespace Fuse.Languages.CSharp.Markers;

/// <summary>
///     Generates semantic marker comments from C# content using regex extraction.
/// </summary>
public sealed class CSharpSemanticMarkerGenerator : ISemanticMarkerGenerator
{
    private static readonly Regex TypeDeclarationRegex = new(
        @"(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|\s)*\b(class|interface|record|enum|struct)\s+(\w+)(?:<[^>]+>)?(?:\s*:\s*([^\{]+))?",
        RegexOptions.Compiled);

    private static readonly Regex ConstructorRegex = new(
        @"\b(\w+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex PropertyRegex = new(
        @"(?:public|private|protected|internal|static|virtual|override|\s)*([\w<>\[\],\?\.\(\)]+)\s+(\w+)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceTypeRegex = new(@"\bI[A-Z]\w*\b", RegexOptions.Compiled);

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<SemanticMarker> GenerateMarkers(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var markers = new List<SemanticMarker>();
        foreach (Match match in TypeDeclarationRegex.Matches(content))
        {
            var kind = match.Groups[1].Value;
            var typeName = match.Groups[2].Value;
            var baseList = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;

            var implements = ParseImplements(baseList, kind);
            var constructorTypes = ExtractConstructorParameterTypes(content, typeName);
            var dependsOn = ExtractDependsOn(content, typeName, constructorTypes);

            markers.Add(new SemanticMarker(
                typeName,
                kind,
                implements,
                dependsOn,
                constructorTypes));
        }

        return markers;
    }

    private static IReadOnlyList<string> ParseImplements(string baseList, string kind)
    {
        if (string.IsNullOrWhiteSpace(baseList))
            return [];

        var parts = baseList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return [];

        if (kind is "interface")
            return parts;

        if (parts.Length == 1)
            return parts;

        var firstIsBase = !parts[0].StartsWith('I') || parts[0].Length < 2 || !char.IsUpper(parts[0][1]);
        return firstIsBase ? parts.Skip(1).ToArray() : parts;
    }

    private static IReadOnlyList<string> ExtractConstructorParameterTypes(string content, string typeName)
    {
        var typeBlock = ExtractTypeBlock(content, typeName);
        if (typeBlock is null)
            return [];

        var ctorMatch = ConstructorRegex.Match(typeBlock);
        if (!ctorMatch.Success || ctorMatch.Groups[1].Value != typeName)
        {
            var ctorPattern = new Regex($@"\b{typeName}\s*\(([^)]*)\)", RegexOptions.Compiled);
            ctorMatch = ctorPattern.Match(typeBlock);
            if (!ctorMatch.Success)
                return [];
        }

        return ParseParameterTypes(ctorMatch.Groups[ctorMatch.Groups.Count - 1].Value);
    }

    private static IReadOnlyList<string> ExtractDependsOn(
        string content,
        string typeName,
        IReadOnlyList<string> constructorTypes)
    {
        var depends = new HashSet<string>(constructorTypes, StringComparer.Ordinal);
        var typeBlock = ExtractTypeBlock(content, typeName);
        if (typeBlock is null)
            return depends.ToArray();

        foreach (Match match in PropertyRegex.Matches(typeBlock))
        {
            var propType = match.Groups[1].Value.Trim();
            if (InterfaceTypeRegex.IsMatch(propType))
            {
                foreach (Match iface in InterfaceTypeRegex.Matches(propType))
                    depends.Add(iface.Value);
            }
        }

        return depends.ToArray();
    }

    private static string? ExtractTypeBlock(string content, string typeName)
    {
        var pattern = new Regex(
            $@"(?:class|interface|record|enum|struct)\s+{typeName}\b[^{{]*\{{",
            RegexOptions.Singleline);
        var match = pattern.Match(content);
        if (!match.Success)
            return null;

        var start = match.Index;
        var depth = 0;
        for (var i = start; i < content.Length; i++)
        {
            if (content[i] == '{')
                depth++;
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return content[start..(i + 1)];
            }
        }

        return content[start..];
    }

    private static IReadOnlyList<string> ParseParameterTypes(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return [];

        return parameters
            .Split(',')
            .Select(p => p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.TrimEnd('?'))
            .Distinct()
            .ToArray();
    }
}
