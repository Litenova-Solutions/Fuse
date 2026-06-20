using System.Text;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Formats fused entries with a minimal, token-lean envelope: a single header line per file and no
///     closing marker. Each entry starts with a <c>=== path ===</c> line; the next header or end of output
///     bounds the body. This trades the self-describing XML wrapper for fewer envelope tokens.
/// </summary>
/// <remarks>
///     XML remains the default format. The compact envelope drops the per-file closing tag and attribute
///     syntax, so the saving grows with the number of files. The body is emitted verbatim; only the wrapper
///     differs.
/// </remarks>
public sealed class CompactEntryFormatter : IEntryFormatter
{
    /// <inheritdoc />
    public string FormatEntry(FusedContent content, EmissionOptions options)
    {
        var sb = new StringBuilder();

        if (options.IncludeProvenance && content.InclusionChain is { Count: > 1 })
        {
            sb.Append("@via ");
            sb.AppendLine(string.Join(" -> ", content.InclusionChain));
        }

        sb.Append("=== ");
        sb.Append(content.NormalizedPath);

        if (options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            sb.Append(" | ");
            sb.Append(fileInfo.Length);
            sb.Append("b ");
            sb.Append(fileInfo.LastWriteTimeUtc.ToString("O"));
        }

        sb.AppendLine(" ===");
        sb.Append(content.Content);

        if (!content.Content.EndsWith('\n'))
            sb.AppendLine();

        sb.AppendLine();

        return sb.ToString();
    }
}
