using System.Text.Json;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Formats fused entries as JSON lines.
/// </summary>
public sealed class JsonEntryFormatter : IEntryFormatter
{
    /// <inheritdoc />
    public string FormatEntry(FusedContent content, EmissionOptions options)
    {
        var entry = new JsonFileEntryDto
        {
            Path = content.NormalizedPath,
            Content = content.Content,
            Tokens = content.TokenCount,
        };

        if (options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            entry.Size = fileInfo.Length;
            entry.Modified = fileInfo.LastWriteTimeUtc.ToString("O");
        }

        if (options.IncludeProvenance && content.InclusionChain is { Count: > 1 })
            entry.Provenance = content.InclusionChain.ToArray();

        return JsonSerializer.Serialize(entry, FuseEmissionJsonContext.Default.JsonFileEntryDto) + "\n";
    }
}
