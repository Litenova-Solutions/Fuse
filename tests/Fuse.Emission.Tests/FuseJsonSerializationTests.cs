using System.Text.Json;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;
using Fuse.Collection.Models;

namespace Fuse.Emission.Tests;

public sealed class FuseJsonSerializationTests
{
    [Fact]
    public void JsonManifest_ContainsExpectedStructure()
    {
        var files = new[] { new FileTokenInfo("A.cs", 42), new FileTokenInfo("B.cs", 100) };

        var json = ManifestBuilder.Build(files, OutputFormat.Json);

        using var document = JsonDocument.Parse(json.Trim());
        var root = document.RootElement;

        Assert.Equal("manifest", root.GetProperty("type").GetString());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.Equal("A.cs", root.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(42, root.GetProperty("files")[0].GetProperty("tokens").GetInt64());
    }

    [Fact]
    public void JsonEntryFormatter_SerializesProvenanceAndMetadata()
    {
        var formatter = new JsonEntryFormatter();
        var content = CreateContent(["Seed.cs", "Dep.cs"], "class Dep {}");

        var output = formatter.FormatEntry(
            content,
            new EmissionOptions { IncludeProvenance = true, IncludeMetadata = true });

        using var document = JsonDocument.Parse(output.Trim());
        var root = document.RootElement;

        Assert.Equal("file", root.GetProperty("type").GetString());
        Assert.Equal("Dep.cs", root.GetProperty("path").GetString());
        Assert.True(root.GetProperty("size").GetInt64() >= 0);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("modified").GetString()));
        Assert.Equal(2, root.GetProperty("provenance").GetArrayLength());
    }

    [Fact]
    public void JsonManifestDto_RoundTripsThroughSourceContext()
    {
        var dto = new JsonManifestDto
        {
            Files =
            [
                new JsonManifestFileDto { Path = "X.cs", Tokens = 10, Commits = 3, LastModified = "2026-01-01" },
            ],
            Patterns = [new JsonPatternDto { Name = "async", Summary = "Task usage" }],
        };

        var json = JsonSerializer.Serialize(dto, FuseEmissionJsonContext.Default.JsonManifestDto);
        var restored = JsonSerializer.Deserialize(json, FuseEmissionJsonContext.Default.JsonManifestDto);

        Assert.NotNull(restored);
        Assert.Equal("X.cs", restored!.Files[0].Path);
        Assert.Equal("async", restored.Patterns![0].Name);
    }

    private static FusedContent CreateContent(IReadOnlyList<string> chain, string body)
    {
        var fullPath = Path.Combine(Path.GetTempPath(), chain[^1]);
        File.WriteAllText(fullPath, body);
        var candidate = new FileCandidate(fullPath, chain[^1], new FileInfo(fullPath));
        var source = new SourceFile(candidate);
        var counter = new TokenizerFactory().GetCounter();
        return new FusedContent(source, body, counter, inclusionChain: chain);
    }
}
