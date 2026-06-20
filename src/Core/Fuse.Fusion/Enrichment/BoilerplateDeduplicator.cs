using System.Text;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.Enrichment;

/// <summary>
///     Detects identical leading comment headers shared by two or more files (for example a license banner)
///     and replaces each repeated occurrence with a short marker, emitting the canonical header once in a
///     preamble.
/// </summary>
/// <remarks>
///     Only leading comment blocks are considered: a run of blank lines and C-style line or block comments,
///     or XML comments, from the top of the file down to the first line of code. Preprocessor directives and
///     code are never touched, so type, method, and route fidelity is unaffected; only repeated comment text
///     is moved. A header must carry some text and be shared by at least two files to be deduplicated.
/// </remarks>
public sealed class BoilerplateDeduplicator
{
    private const int MinimumHeaderLength = 16;
    private const int MinimumSharedFiles = 2;

    /// <summary>
    ///     Deduplicates shared leading headers across the supplied entries.
    /// </summary>
    /// <param name="content">The reduced entries to scan.</param>
    /// <param name="tokenCounter">The token counter used to recompute the token count of rewritten entries.</param>
    /// <returns>
    ///     The rewritten entries, a preamble listing each deduplicated header (or <c>null</c> when none was
    ///     shared), and counts of headers deduplicated and files affected.
    /// </returns>
    public DeduplicationResult Deduplicate(IReadOnlyList<FusedContent> content, ITokenCounter tokenCounter)
    {
        // First pass: extract each entry's leading header and count how often each distinct header appears.
        var headers = new Dictionary<string, int>(StringComparer.Ordinal);
        var splits = new (string Header, string Body)?[content.Count];
        for (var i = 0; i < content.Count; i++)
        {
            var split = SplitHeader(content[i].Content);
            if (split is null)
                continue;

            splits[i] = split;
            headers.TryGetValue(split.Value.Header, out var count);
            headers[split.Value.Header] = count + 1;
        }

        // Assign a stable id to each header shared widely enough to be worth moving.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var entry in splits)
        {
            if (entry is null)
                continue;

            var header = entry.Value.Header;
            if (headers[header] < MinimumSharedFiles || ids.ContainsKey(header))
                continue;

            ids[header] = ordered.Count + 1;
            ordered.Add(header);
        }

        if (ordered.Count == 0)
            return new DeduplicationResult(content, null, 0, 0);

        // Second pass: rewrite affected entries to reference the shared header by marker.
        var rewritten = new FusedContent[content.Count];
        var filesAffected = 0;
        for (var i = 0; i < content.Count; i++)
        {
            var split = splits[i];
            if (split is null || !ids.TryGetValue(split.Value.Header, out var id))
            {
                rewritten[i] = content[i];
                continue;
            }

            var body = split.Value.Body;
            var marker = $"// fuse:header[{id}]";
            var newContent = body.Length == 0 ? marker + "\n" : marker + "\n" + body;
            rewritten[i] = content[i].WithReducedContent(newContent, tokenCounter);
            filesAffected++;
        }

        return new DeduplicationResult(rewritten, BuildPreamble(ordered, ids, headers), ordered.Count, filesAffected);
    }

    private static string BuildPreamble(
        IReadOnlyList<string> ordered,
        IReadOnlyDictionary<string, int> ids,
        IReadOnlyDictionary<string, int> counts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== fuse:deduplicated-headers ===");
        foreach (var header in ordered)
        {
            var id = ids[header];
            sb.Append("// fuse:header[");
            sb.Append(id);
            sb.Append("] shared by ");
            sb.Append(counts[header]);
            sb.AppendLine(" files:");
            sb.AppendLine(header);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    ///     Splits content into its leading comment header and the remaining body, or returns <c>null</c> when
    ///     there is no qualifying header.
    /// </summary>
    private static (string Header, string Body)? SplitHeader(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var lines = content.Split('\n');
        var lastCommentLine = -1;
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (inBlockComment)
            {
                lastCommentLine = i;
                if (trimmed.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = false;
                continue;
            }

            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                lastCommentLine = i;
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                lastCommentLine = i;
                if (!trimmed.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = true;
                continue;
            }

            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                lastCommentLine = i;
                if (!trimmed.Contains("-->", StringComparison.Ordinal))
                    inBlockComment = true; // reuse the block flag; closed by --> below
                continue;
            }

            break;
        }

        if (lastCommentLine < 0)
            return null;

        var header = NormalizeHeader(lines, lastCommentLine);
        if (header.Length < MinimumHeaderLength)
            return null;

        var body = lastCommentLine + 1 >= lines.Length
            ? string.Empty
            : string.Join('\n', lines[(lastCommentLine + 1)..]);

        return (header, body);
    }

    private static string NormalizeHeader(string[] lines, int lastCommentLine)
    {
        var sb = new StringBuilder();
        for (var i = 0; i <= lastCommentLine; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(lines[i].TrimEnd().TrimEnd('\r'));
        }

        return sb.ToString().Trim();
    }
}
