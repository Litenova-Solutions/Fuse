using System.Text;
using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Rebuilds a file as a thin host skeleton: the members a query selected are kept verbatim and every other
///     member is collapsed to its signature, while type headers, usings, and braces around them are preserved.
///     This moves the unit of inclusion from the whole file to the members a task actually needs.
/// </summary>
internal static class ThinSkeletonAssembler
{
    /// <summary>
    ///     Assembles the thin skeleton of <paramref name="content" /> given its member chunks and the set of
    ///     qualified member names to keep in full.
    /// </summary>
    /// <param name="content">The (already reduced) file content to slice.</param>
    /// <param name="chunks">The member chunks extracted from <paramref name="content" />, in any order.</param>
    /// <param name="selectedQualifiedNames">The <c>Type.Member</c> names to keep verbatim.</param>
    /// <returns>
    ///     The skeleton text, or the original content unchanged when there are no chunks to collapse. Selected
    ///     members are emitted byte-for-byte so they stay independently parseable.
    /// </returns>
    public static string Assemble(
        string content,
        IReadOnlyList<SymbolChunk> chunks,
        IReadOnlySet<string> selectedQualifiedNames)
    {
        if (chunks.Count == 0)
            return content;

        var lines = SplitLines(content);
        var ordered = chunks.OrderBy(c => c.StartLine).ToList();
        var sb = new StringBuilder(content.Length);
        var cursor = 1; // 1-based index of the next source line to emit.

        foreach (var chunk in ordered)
        {
            // Skip chunks that overlap one already emitted (a nested member inside a kept body, for example).
            if (chunk.StartLine < cursor || chunk.StartLine > lines.Length)
                continue;

            // Gap lines before this member: type headers, usings, attributes, blank lines, closing braces.
            for (var l = cursor; l < chunk.StartLine; l++)
                AppendLine(sb, lines[l - 1]);

            var keepFull = selectedQualifiedNames.Contains(chunk.QualifiedName)
                || chunk.SymbolKind is "field" or "enum-member";

            if (keepFull)
            {
                var end = Math.Min(chunk.EndLine, lines.Length);
                for (var l = chunk.StartLine; l <= end; l++)
                    AppendLine(sb, lines[l - 1]);
            }
            else
            {
                AppendLine(sb, CollapseSignature(lines, chunk));
            }

            cursor = chunk.EndLine + 1;
        }

        for (var l = cursor; l <= lines.Length; l++)
            AppendLine(sb, lines[l - 1]);

        return sb.ToString().TrimEnd('\n');
    }

    // Collapses a member to its signature: everything up to the body opener ('{' or '=>'), normalized onto one
    // line, with the original leading indent and a terminating ';'. Never drops the declared name or its type.
    private static string CollapseSignature(string[] lines, SymbolChunk chunk)
    {
        var first = lines[chunk.StartLine - 1];
        var indent = first[..(first.Length - first.TrimStart().Length)];

        var end = Math.Min(chunk.EndLine, lines.Length);
        var joined = string.Join(' ', lines[(chunk.StartLine - 1)..end].Select(l => l.Trim()));

        var brace = joined.IndexOf('{');
        var arrow = joined.IndexOf("=>", StringComparison.Ordinal);
        var cut = MinNonNegative(brace, arrow);

        var signature = (cut >= 0 ? joined[..cut] : joined).Trim();
        if (!signature.EndsWith(';'))
            signature += ";";

        return indent + signature;
    }

    private static int MinNonNegative(int a, int b)
    {
        if (a < 0) return b;
        if (b < 0) return a;
        return Math.Min(a, b);
    }

    // Output always uses '\n' so the skeleton is deterministic regardless of the source's line endings.
    private static void AppendLine(StringBuilder sb, string line) => sb.Append(line).Append('\n');

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
}
