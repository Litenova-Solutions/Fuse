using Fuse.Languages.Abstractions.Dependencies;
using System.Text.RegularExpressions;

namespace Fuse.Languages.CSharp.Dependencies;

/// <summary>
///     Regex-based C# dependency extractor. Produces a best-effort approximation of referenced types;
///     may miss dynamically dispatched dependencies or produce false positives from type names in comments.
/// </summary>
public sealed partial class CSharpDependencyExtractor : IDependencyExtractor
{
    private static readonly HashSet<string> Primitives = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "int", "bool", "long", "short", "byte", "char", "float", "double", "decimal",
        "void", "object", "uint", "ulong", "ushort", "sbyte", "nint", "nuint",
    };

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractReferencedTypes(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var types = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in BaseTypeRegex().Matches(content))
            AddTypeTokens(types, match.Groups[1].Value);

        foreach (Match match in ParameterListRegex().Matches(content))
            AddParameterTypes(types, match.Groups[1].Value);

        foreach (Match match in BracedBlockRegex().Matches(content))
        {
            var propMatch = PropertyInBlockRegex().Match(match.Value);
            if (propMatch.Success)
                AddTypeTokens(types, propMatch.Groups[1].Value);
        }

        foreach (Match match in FieldDeclarationRegex().Matches(content))
            AddTypeTokens(types, match.Groups[1].Value);

        return types.ToArray();
    }

    private static void AddParameterTypes(HashSet<string> types, string parameters)
    {
        foreach (var part in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
                AddTypeTokens(types, tokens[0]);
        }
    }

    private static void AddTypeTokens(HashSet<string> types, string text)
    {
        foreach (Match match in TypeTokenRegex().Matches(text))
        {
            var name = match.Groups[1].Value;
            var simple = name.Contains('<') ? name[..name.IndexOf('<')] : name;
            if (!Primitives.Contains(simple))
                types.Add(simple);
        }
    }

    [GeneratedRegex(@"(?:class|record|struct)\s+\w+(?:<[^>]+>)?\s*:\s*([^\{]+)", RegexOptions.Compiled)]
    private static partial Regex BaseTypeRegex();

    [GeneratedRegex(@"\(([^)]*)\)", RegexOptions.Compiled)]
    private static partial Regex ParameterListRegex();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.Compiled)]
    private static partial Regex BracedBlockRegex();

    [GeneratedRegex(@"([\w<>\[\],\?\.\(\)]+)\s+\w+\s*\{", RegexOptions.Compiled)]
    private static partial Regex PropertyInBlockRegex();

    [GeneratedRegex(@"(?:public|private|protected|internal|readonly)\s+([\w<>\[\],\?\.\(\)]+)\s+\w+\s*[;=]", RegexOptions.Compiled)]
    private static partial Regex FieldDeclarationRegex();

    [GeneratedRegex(@"\b([A-Z]\w*(?:<[^>]+>)?)\b", RegexOptions.Compiled)]
    private static partial Regex TypeTokenRegex();
}
