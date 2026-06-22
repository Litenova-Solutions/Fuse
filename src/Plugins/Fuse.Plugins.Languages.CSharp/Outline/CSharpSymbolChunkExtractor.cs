using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Outline;

/// <summary>
///     Extracts member-level <see cref="SymbolChunk" />s from <c>.cs</c> content using regex and brace-depth
///     scanning. This is the Native AOT fallback for symbol-level retrieval, used when the Roslyn extractor is
///     not registered.
/// </summary>
/// <remarks>
///     Structure is scanned over a sanitized copy (comments and literals blanked by
///     <see cref="CSharpSourceSanitizer" />, which preserves character and line positions) so braces inside
///     strings do not skew the depth counter, while chunk bodies are sliced from the original text. Boundaries
///     are coarser than the Roslyn extractor: members whose opening brace falls on a later line, and plain
///     fields without a brace or parenthesis, are not chunked. Every chunk it does produce is a contiguous,
///     brace-balanced span, so member bodies remain independently parseable.
/// </remarks>
public sealed partial class CSharpSymbolChunkExtractor : ISymbolChunkExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<SymbolChunk> ExtractChunks(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        var original = SplitLines(content);
        var scan = SplitLines(CSharpSourceSanitizer.Sanitize(content));
        var chunks = new List<SymbolChunk>();

        // Stack of open types so members attribute to the innermost enclosing type. Each frame records the
        // brace depth at which the type opened; its body sits at that depth + 1.
        var openTypes = new Stack<TypeFrame>();
        var depth = 0;
        var i = 0;

        while (i < scan.Length)
        {
            var line = scan[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            var typeMatch = TypeDeclarationRegex().Match(trimmed);
            if (typeMatch.Success)
            {
                openTypes.Push(new TypeFrame(typeMatch.Groups[2].Value, depth, typeMatch.Groups[1].Value == "enum"));
                depth += NetBraces(line);
                i++;
                continue;
            }

            if (openTypes.Count > 0 && depth == openTypes.Peek().Depth + 1)
            {
                var frame = openTypes.Peek();
                if (frame.IsEnum)
                {
                    foreach (Match m in EnumMemberRegex().Matches(trimmed))
                    {
                        var name = m.Groups[1].Value;
                        if (!IsKeyword(name))
                            chunks.Add(new SymbolChunk("enum-member", name, frame.Name, original[i].Trim(), i + 1, i + 1));
                    }

                    depth += NetBraces(line);
                    i = AdvanceAndPop(ref depth, openTypes, i);
                    continue;
                }

                var member = ExtractMember(trimmed, frame.Name);
                if (member is not null)
                {
                    var end = FindMemberEnd(scan, i, depth);
                    var body = string.Join('\n', original[i..(end + 1)]).Trim();
                    chunks.Add(new SymbolChunk(member.Kind, member.Name, frame.Name, body, i + 1, end + 1));

                    // Recompute depth across the consumed span and pop any types it closed.
                    for (var k = i; k <= end; k++)
                        depth += NetBraces(scan[k]);
                    if (depth < 0)
                        depth = 0;
                    while (openTypes.Count > 0 && depth <= openTypes.Peek().Depth)
                        openTypes.Pop();

                    i = end + 1;
                    continue;
                }
            }

            depth += NetBraces(line);
            i = AdvanceAndPop(ref depth, openTypes, i);
        }

        return chunks;
    }

    // Advances past line i, clamping depth and popping any type frames whose closing brace was passed.
    private static int AdvanceAndPop(ref int depth, Stack<TypeFrame> openTypes, int i)
    {
        if (depth < 0)
            depth = 0;
        while (openTypes.Count > 0 && depth <= openTypes.Peek().Depth)
            openTypes.Pop();
        return i + 1;
    }

    // Walks forward from the member's declaration line to its last line. A brace-bodied member ends when depth
    // returns to the type-body level; a semicolon-terminated member (field, abstract method, single-line
    // expression body) ends at the first line ending in ';' before any brace opens.
    private static int FindMemberEnd(string[] scan, int start, int bodyDepth)
    {
        var depth = bodyDepth;
        var braceSeen = false;
        for (var j = start; j < scan.Length; j++)
        {
            if (scan[j].Contains('{'))
                braceSeen = true;

            depth += NetBraces(scan[j]);

            if (braceSeen && depth <= bodyDepth)
                return j;
            if (!braceSeen && scan[j].TrimEnd().EndsWith(';'))
                return j;
        }

        return scan.Length - 1;
    }

    private static MemberMatch? ExtractMember(string trimmed, string parentType)
    {
        if (trimmed is "{" or "}" or "};")
            return null;
        if (trimmed.StartsWith('[') || trimmed.StartsWith("//", StringComparison.Ordinal))
            return null;

        var method = MethodRegex().Match(trimmed);
        if (method.Success && !IsKeyword(method.Groups[1].Value))
        {
            var name = method.Groups[1].Value;
            return new MemberMatch(name == parentType ? "constructor" : "method", name);
        }

        var property = PropertyRegex().Match(trimmed);
        if (property.Success && !IsKeyword(property.Groups[1].Value))
            return new MemberMatch("property", property.Groups[1].Value);

        return null;
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

    private static string[] SplitLines(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            var end = i > start && content[i - 1] == '\r' ? i - 1 : i;
            lines.Add(content[start..end]);
            start = i + 1;
        }

        if (start <= content.Length)
            lines.Add(content[start..]);

        return [.. lines];
    }

    private sealed record TypeFrame(string Name, int Depth, bool IsEnum);

    private sealed record MemberMatch(string Kind, string Name);

    [GeneratedRegex(@"^\s*(?:[\w\s\.\[\]]+\s+)?(class|interface|record|struct|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*(?:\{|=>)", RegexOptions.Compiled)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*(?:=[^,]+)?(?:,|$)", RegexOptions.Compiled)]
    private static partial Regex EnumMemberRegex();
}
