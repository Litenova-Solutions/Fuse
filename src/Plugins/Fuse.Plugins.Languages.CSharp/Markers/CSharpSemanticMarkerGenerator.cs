using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Markers;

namespace Fuse.Plugins.Languages.CSharp.Markers;

/// <summary>
///     Generates one <see cref="SemanticMarker" /> per type declaration in <c>.cs</c> content, capturing the
///     type kind, implemented interfaces or base types, constructor parameter types, and inferred dependencies.
/// </summary>
/// <remarks>
///     Extraction is regex- and brace-scan-based rather than a real parse, so results are heuristic: the base
///     class is distinguished from interfaces by the <c>I</c>-prefix naming convention, dependencies are
///     inferred from constructor parameters and interface-typed properties, and declarations inside comments
///     or strings may be misread. Treat the markers as approximate structural hints.
/// </remarks>
public sealed partial class CSharpSemanticMarkerGenerator : ISemanticMarkerGenerator
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<SemanticMarker> GenerateMarkers(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var markers = new List<SemanticMarker>();
        foreach (Match match in TypeDeclarationRegex().Matches(content))
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

        var ctorMatch = ConstructorRegex().Match(typeBlock);
        if (!ctorMatch.Success || ctorMatch.Groups[1].Value != typeName)
        {
            ctorMatch = FindNamedConstructorMatch(typeBlock, typeName);
            if (!ctorMatch.Success)
                return [];
        }

        return ParseParameterTypes(ctorMatch.Groups[ctorMatch.Groups.Count - 1].Value);
    }

    private static Match FindNamedConstructorMatch(string typeBlock, string typeName)
    {
        var searchStart = 0;
        while (searchStart < typeBlock.Length)
        {
            var index = typeBlock.IndexOf(typeName, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return Match.Empty;

            if (IsWordBoundary(typeBlock, index, typeName.Length) &&
                index + typeName.Length < typeBlock.Length &&
                typeBlock[index + typeName.Length] == '(')
            {
                var parametersStart = index + typeName.Length + 1;
                var parametersEnd = typeBlock.IndexOf(')', parametersStart);
                if (parametersEnd < 0)
                    return Match.Empty;

                var parameters = typeBlock[parametersStart..parametersEnd];
                return ConstructorRegex().Match($"{typeName}({parameters})");
            }

            searchStart = index + typeName.Length;
        }

        return Match.Empty;
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

        foreach (Match match in PropertyRegex().Matches(typeBlock))
        {
            var propType = match.Groups[1].Value.Trim();
            if (InterfaceTypeRegex().IsMatch(propType))
            {
                foreach (Match iface in InterfaceTypeRegex().Matches(propType))
                    depends.Add(iface.Value);
            }
        }

        return depends.ToArray();
    }

    private static string? ExtractTypeBlock(string content, string typeName)
    {
        var searchStart = 0;
        while (searchStart < content.Length)
        {
            var index = content.IndexOf(typeName, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return null;

            if (!IsTypeDeclarationMatch(content, index, typeName))
            {
                searchStart = index + typeName.Length;
                continue;
            }

            var braceIndex = content.IndexOf('{', index + typeName.Length);
            if (braceIndex < 0)
                return null;

            var start = index;
            var depth = 0;
            for (var i = braceIndex; i < content.Length; i++)
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

        return null;
    }

    private static bool IsTypeDeclarationMatch(string content, int typeNameIndex, string typeName)
    {
        if (!IsWordBoundary(content, typeNameIndex, typeName.Length))
            return false;

        var prefix = content[..typeNameIndex];
        return prefix.Contains("class ", StringComparison.Ordinal) ||
               prefix.Contains("interface ", StringComparison.Ordinal) ||
               prefix.Contains("record ", StringComparison.Ordinal) ||
               prefix.Contains("enum ", StringComparison.Ordinal) ||
               prefix.Contains("struct ", StringComparison.Ordinal);
    }

    private static bool IsWordBoundary(string content, int index, int length)
    {
        if (index > 0 && char.IsLetterOrDigit(content[index - 1]))
            return false;

        var end = index + length;
        return end >= content.Length || !char.IsLetterOrDigit(content[end]);
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

    [GeneratedRegex(
        @"(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|\s)*\b(class|interface|record|enum|struct)\s+(\w+)(?:<[^>]+>)?(?:\s*:\s*([^\{]+))?",
        RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"\b(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled)]
    private static partial Regex ConstructorRegex();

    [GeneratedRegex(
        @"(?:public|private|protected|internal|static|virtual|override|\s)*([\w<>\[\],\?\.\(\)]+)\s+(\w+)\s*\{",
        RegexOptions.Compiled)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"\bI[A-Z]\w*\b", RegexOptions.Compiled)]
    private static partial Regex InterfaceTypeRegex();
}
