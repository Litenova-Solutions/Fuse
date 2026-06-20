using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Outline;

/// <summary>
///     Extracts a structural outline (types and their members) from <c>.cs</c> content using regex and
///     brace-depth scanning.
/// </summary>
/// <remarks>
///     Comments and literals are blanked before scanning (see <see cref="CSharpSourceSanitizer" />), so braces
///     inside strings do not skew depth tracking. As with the skeleton extractor, brace counting is line-based
///     and not preprocessor-aware, so heavy conditional compilation can still desynchronize the depth counter.
///     A semantic (Roslyn) outline extractor registered later replaces this one by extension.
/// </remarks>
public sealed partial class CSharpOutlineExtractor : ISymbolOutlineExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<OutlineSymbol> ExtractOutline(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        var sanitized = CSharpSourceSanitizer.Sanitize(content);
        var symbols = new List<OutlineSymbol>();

        // Stack of open types so members are attributed to the innermost enclosing type. Each frame records
        // the brace depth at which the type opened, so a closing brace pops the right frame.
        var openTypes = new Stack<TypeFrame>();
        var depth = 0;

        foreach (var rawLine in EnumerateLines(sanitized))
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.Length == 0)
                continue;

            var typeMatch = TypeDeclarationRegex().Match(trimmed);
            if (typeMatch.Success)
            {
                var kind = typeMatch.Groups[1].Value;
                var name = typeMatch.Groups[2].Value;
                var frame = new TypeFrame(kind, name, depth, kind == "enum", []);
                symbols.Add(new OutlineSymbol(kind, name, frame.Members));
                openTypes.Push(frame);
                depth += NetBraces(trimmed);
                continue;
            }

            if (openTypes.Count > 0 && depth == openTypes.Peek().Depth + 1)
            {
                var current = openTypes.Peek();
                if (current.IsEnum)
                    AddEnumMembers(trimmed, current.Members);
                else
                {
                    var member = ExtractMemberName(trimmed);
                    if (member is not null)
                        current.Members.Add(member);
                }
            }

            depth += NetBraces(trimmed);
            if (depth < 0)
                depth = 0;

            // Pop any type frames whose closing brace we have now passed.
            while (openTypes.Count > 0 && depth <= openTypes.Peek().Depth)
                openTypes.Pop();
        }

        return symbols;
    }

    // Members of a type body declared directly at depth+1. Returns the member name, or null for braces,
    // attributes, and lines that carry no recognizable declaration.
    private static string? ExtractMemberName(string trimmed)
    {
        if (trimmed is "{" or "}" or "};")
            return null;

        if (trimmed.StartsWith('[') || trimmed.StartsWith("//", StringComparison.Ordinal))
            return null;

        var method = MethodRegex().Match(trimmed);
        if (method.Success && !IsKeyword(method.Groups[1].Value))
            return method.Groups[1].Value;

        var property = PropertyRegex().Match(trimmed);
        if (property.Success && !IsKeyword(property.Groups[1].Value))
            return property.Groups[1].Value;

        return null;
    }

    // Enum members are comma-separated identifiers, optionally with explicit values; capture the names.
    private static void AddEnumMembers(string trimmed, List<string> members)
    {
        foreach (Match match in EnumMemberRegex().Matches(trimmed))
        {
            var name = match.Groups[1].Value;
            if (!IsKeyword(name))
                members.Add(name);
        }
    }

    private static int NetBraces(string text)
    {
        var net = 0;
        foreach (var ch in text)
        {
            if (ch == '{')
                net++;
            else if (ch == '}')
                net--;
        }

        return net;
    }

    private static bool IsKeyword(string token) => token switch
    {
        "if" or "for" or "foreach" or "while" or "switch" or "using" or "return" or "lock" or "fixed" or
        "catch" or "get" or "set" or "init" or "add" or "remove" or "where" or "new" or "throw" => true,
        _ => false,
    };

    private static IEnumerable<string> EnumerateLines(string content)
    {
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            var end = i > start && content[i - 1] == '\r' ? i - 1 : i;
            yield return content[start..end];
            start = i + 1;
        }

        if (start < content.Length)
            yield return content[start..];
    }

    private sealed record TypeFrame(string Kind, string Name, int Depth, bool IsEnum, List<string> Members);

    [GeneratedRegex(@"^\s*(?:[\w\s\.\[\]]+\s+)?(class|interface|record|struct|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    // A member name immediately preceding an opening parenthesis: methods, constructors, and local functions.
    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex MethodRegex();

    // A member name immediately preceding a property body or expression body.
    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*(?:\{|=>)", RegexOptions.Compiled)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*(?:=[^,]+)?(?:,|$)", RegexOptions.Compiled)]
    private static partial Regex EnumMemberRegex();
}
