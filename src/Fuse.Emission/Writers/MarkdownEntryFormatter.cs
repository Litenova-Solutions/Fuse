using System.Text;
using System.Text.Json;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Formats fused entries as Markdown sections with fenced code blocks.
/// </summary>
public sealed class MarkdownEntryFormatter : IEntryFormatter
{
    /// <inheritdoc />
    public string FormatEntry(FusedContent content, EmissionOptions options)
    {
        var sb = new StringBuilder();

        if (options.IncludeProvenance && content.InclusionChain is { Count: > 1 })
        {
            sb.Append("<!-- included via: ");
            sb.Append(string.Join(" -> ", content.InclusionChain));
            sb.AppendLine(" -->");
        }

        sb.Append("### ");
        sb.AppendLine(content.NormalizedPath);

        if (options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            sb.Append("*size: ");
            sb.Append(fileInfo.Length);
            sb.Append(", modified: ");
            sb.Append(fileInfo.LastWriteTimeUtc.ToString("O"));
            sb.AppendLine("*");
            sb.AppendLine();
        }

        sb.AppendLine("```");
        sb.Append(content.Content);

        if (!content.Content.EndsWith('\n'))
        {
            sb.AppendLine();
        }

        sb.AppendLine("```");
        sb.AppendLine();

        return sb.ToString();
    }
}
