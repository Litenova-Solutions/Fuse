using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;
using Fuse.Collection.Models;

namespace Fuse.Emission.Tests;

public sealed class ManifestBuilderTests
{
    [Fact]
    public void Build_XmlManifest_IncludesFileTreeAndTokens()
    {
        var files = new[]
        {
            new FileTokenInfo("B.cs", 1200),
            new FileTokenInfo("A.cs", 10),
        };

        var manifest = ManifestBuilder.Build(files, OutputFormat.Xml);

        Assert.Contains("fuse:manifest", manifest);
        Assert.Contains("files: 2", manifest);
        Assert.Contains("A.cs (~10 tokens)", manifest);
        Assert.Contains("B.cs (~1.2k tokens)", manifest);
    }

    [Fact]
    public void Build_JsonManifest_EmitsManifestObject()
    {
        var files = new[] { new FileTokenInfo("A.cs", 42) };

        var manifest = ManifestBuilder.Build(files, OutputFormat.Json);

        Assert.Contains("\"type\":\"manifest\"", manifest.Replace(" ", string.Empty));
        Assert.Contains("\"path\":\"A.cs\"", manifest);
    }
}

public sealed class EntryFormatterTests
{
    [Fact]
    public void MarkdownFormatter_IncludesProvenanceComment()
    {
        var formatter = new MarkdownEntryFormatter();
        var content = CreateContent(["Seed.cs", "Dep.cs"], "class Dep {}");

        var output = formatter.FormatEntry(content, new EmissionOptions { IncludeProvenance = true });

        Assert.Contains("<!-- included via: Seed.cs -> Dep.cs -->", output);
        Assert.Contains("### Dep.cs", output);
    }

    [Fact]
    public void JsonFormatter_IncludesProvenanceField()
    {
        var formatter = new JsonEntryFormatter();
        var content = CreateContent(["Seed.cs", "Dep.cs"], "class Dep {}");

        var output = formatter.FormatEntry(content, new EmissionOptions { IncludeProvenance = true });

        Assert.Contains("\"provenance\":[\"Seed.cs\",\"Dep.cs\"]", output.Replace(" ", string.Empty));
    }

    [Fact]
    public void EntryFormatterFactory_ParsesFormatNames()
    {
        Assert.Equal(OutputFormat.Markdown, EntryFormatterFactory.ParseFormat("markdown"));
        Assert.Equal(OutputFormat.Json, EntryFormatterFactory.ParseFormat("json"));
        Assert.Equal(OutputFormat.Xml, EntryFormatterFactory.ParseFormat("xml"));
    }

    private static FusedContent CreateContent(IReadOnlyList<string> chain, string body)
    {
        var fullPath = Path.Combine(Path.GetTempPath(), chain[^1]);
        var candidate = new FileCandidate(fullPath, chain[^1], new FileInfo(fullPath));
        var source = new SourceFile(candidate);
        var counter = new TokenizerFactory().GetCounter();
        return new FusedContent(source, body, counter, inclusionChain: chain);
    }
}

public sealed class TokenizerFactoryTests
{
    [Fact]
    public void GetCounter_DefaultsToO200kBase()
    {
        var factory = new TokenizerFactory();
        var counter = factory.GetCounter();

        Assert.True(counter.Count("hello world") > 0);
    }

    [Fact]
    public void ResolveEncoding_MapsModelNames()
    {
        Assert.Equal("o200k_base", TokenizerFactory.ResolveEncoding("gpt-4o"));
        Assert.Equal("cl100k_base", TokenizerFactory.ResolveEncoding("gpt-4"));
    }
}
