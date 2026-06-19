using System.Text.Json;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Formats fused entries as JSON lines.
/// </summary>
public sealed class JsonEntryFormatter : IEntryFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <inheritdoc />
    public string FormatEntry(FusedContent content, EmissionOptions options)
    {
        var entry = new Dictionary<string, object?>
        {
            ["type"] = "file",
            ["path"] = content.NormalizedPath,
            ["content"] = content.Content,
            ["tokens"] = content.TokenCount,
        };

        if (options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            entry["size"] = fileInfo.Length;
            entry["modified"] = fileInfo.LastWriteTimeUtc.ToString("O");
        }

        if (options.IncludeProvenance && content.InclusionChain is { Count: > 1 })
        {
            entry["provenance"] = content.InclusionChain;
        }

        return JsonSerializer.Serialize(entry, SerializerOptions) + "\n";
    }
}
