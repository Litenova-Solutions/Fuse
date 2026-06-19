using System.Text;
using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Skeleton;

namespace Fuse.Plugins.Languages.CSharp.Skeleton;

/// <summary>
///     Extracts structural skeletons from reduced C# content using regex and brace-depth scanning, keeping
///     type and member signatures while replacing member bodies with a <c>// ...</c> placeholder.
/// </summary>
/// <remarks>
///     Brace counting is line-based and not string- or comment-aware, so braces inside string literals or
///     verbatim text can skew the depth tracking and drop or retain the wrong lines. The extractor expects
///     content that has already passed through <see cref="Reducers.CSharpReducer" />; raw source with unusual
///     formatting may produce an imperfect skeleton.
/// </remarks>
public sealed partial class CSharpSkeletonExtractor : ISkeletonExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public string ExtractSkeleton(string content, bool publicApiOnly = false)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var result = new StringBuilder();
        var depth = 0;
        var inEnum = false;
        var remaining = content.AsSpan();

        // depth 0 = outside types, 1 = inside type body, >= 2 = inside member bodies (skipped).
        while (!remaining.IsEmpty)
        {
            var newlineIndex = remaining.IndexOf('\n');
            var rawLineSpan = newlineIndex >= 0 ? remaining[..newlineIndex] : remaining;
            remaining = newlineIndex >= 0 ? remaining[(newlineIndex + 1)..] : ReadOnlySpan<char>.Empty;

            var line = rawLineSpan.TrimEnd('\r').ToString();
            var trimmed = line.TrimStart();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (depth <= 1)
                    result.AppendLine();
                continue;
            }

            if (UsingRegex().IsMatch(trimmed) && depth == 0)
            {
                result.AppendLine(line);
                continue;
            }

            if (depth == 0 && TypeDeclarationRegex().IsMatch(trimmed))
            {
                inEnum = trimmed.Contains(" enum ", StringComparison.Ordinal);
                result.AppendLine(line);
                depth += CountChar(trimmed, '{') - CountChar(trimmed, '}');
                continue;
            }

            var openBraces = CountChar(trimmed, '{');
            var closeBraces = CountChar(trimmed, '}');

            if (depth == 1 && inEnum)
            {
                result.AppendLine(line);
                depth += openBraces - closeBraces;
                continue;
            }

            if (depth == 1 && IsSignatureLine(trimmed))
            {
                if (publicApiOnly && IsNonPublicMember(trimmed))
                {
                    depth += openBraces - closeBraces;
                    continue;
                }

                var signature = ExtractSignatureOnly(trimmed);
                result.AppendLine(signature);
                if (!signature.Contains('{') && !signature.EndsWith(';'))
                    result.AppendLine("    // ...");
                else if (openBraces > closeBraces)
                    result.AppendLine("    // ...");

                depth += openBraces - closeBraces;
                continue;
            }

            if (depth >= 2)
            {
                depth += openBraces - closeBraces;
                continue;
            }

            if (depth == 1 && openBraces > closeBraces)
                result.AppendLine("    // ...");

            depth += openBraces - closeBraces;
            if (depth < 0)
                depth = 0;
            if (depth == 0)
                inEnum = false;
        }

        return result.ToString().TrimEnd();
    }

    private static bool IsNonPublicMember(string trimmed)
    {
        if (trimmed.Contains(" private ", StringComparison.Ordinal) ||
            trimmed.Contains(" internal ", StringComparison.Ordinal) ||
            trimmed.StartsWith("private ", StringComparison.Ordinal) ||
            trimmed.StartsWith("internal ", StringComparison.Ordinal))
        {
            return true;
        }

        return !trimmed.Contains(" public ", StringComparison.Ordinal) &&
               !trimmed.Contains(" protected ", StringComparison.Ordinal) &&
               !trimmed.StartsWith("public ", StringComparison.Ordinal) &&
               !trimmed.StartsWith("protected ", StringComparison.Ordinal);
    }

    private static bool IsSignatureLine(string trimmed)
    {
        if (trimmed is "{" or "}" or "};")
            return false;

        if (trimmed.StartsWith('['))
            return false;

        return trimmed.Contains('(') || trimmed.Contains(';') || trimmed.Contains("get;") ||
               trimmed.Contains("set;") || trimmed.Contains("=>") ||
               SignatureKeywordRegex().IsMatch(trimmed);
    }

    private static string ExtractSignatureOnly(string trimmed)
    {
        if (trimmed.Contains("get;", StringComparison.Ordinal) && trimmed.Contains('}'))
            return trimmed;

        var braceIndex = trimmed.IndexOf('{');
        if (braceIndex >= 0)
        {
            var beforeBrace = trimmed[..braceIndex].TrimEnd();
            return beforeBrace + " {";
        }

        return trimmed;
    }

    private static int CountChar(string text, char c)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == c)
                count++;
        }

        return count;
    }

    [GeneratedRegex(@"^\s*using\s+", RegexOptions.Compiled)]
    private static partial Regex UsingRegex();

    [GeneratedRegex(
        @"^\s*(?:[\w\s\.]+\s+)?(class|interface|record|enum|struct|namespace)\s+\w+",
        RegexOptions.Compiled)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"^\s*(?:public|private|protected|internal|static|async|override|virtual)\b", RegexOptions.Compiled)]
    private static partial Regex SignatureKeywordRegex();
}
