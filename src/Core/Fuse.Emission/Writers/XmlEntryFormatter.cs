using System.Text;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Formats fused entries as XML file blocks (default emission format).
/// </summary>
public sealed class XmlEntryFormatter : IEntryFormatter
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

        if (options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            sb.AppendLine(
                $"<file path=\"{content.NormalizedPath}\" size=\"{fileInfo.Length}\" modified=\"{fileInfo.LastWriteTimeUtc:O}\">");
        }
        else
        {
            sb.AppendLine($"<file path=\"{content.NormalizedPath}\">");
        }

        sb.Append(content.Content);

        if (!content.Content.EndsWith('\n'))
        {
            sb.AppendLine();
        }

        sb.AppendLine("</file>");

        return sb.ToString();
    }
}
