using System.Text.Json;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;

namespace Fuse.Emission.Tests;

public sealed class RunReportBuilderTests
{
    [Fact]
    public void Build_ProducesMachineReadableReport()
    {
        var result = new FusionResult(
            generatedPaths: ["out_part1.txt"],
            inMemoryContent: null,
            totalTokens: 1234,
            processedFileCount: 3,
            totalFileCount: 5,
            duration: TimeSpan.FromSeconds(1.5),
            topTokenFiles: [],
            patternSummary: null,
            reductionCacheHits: 2,
            reductionCacheMisses: 1,
            emittedFileTokens: [new FileTokenInfo("A.cs", 100), new FileTokenInfo("B.cs", 200)]);

        var options = new EmissionOptions { TokenizerModel = "o200k_base", Format = OutputFormat.Compact };

        var json = RunReportBuilder.Build(result, options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("report", root.GetProperty("type").GetString());
        Assert.Equal("o200k_base", root.GetProperty("tokenizer").GetString());
        Assert.Equal("compact", root.GetProperty("format").GetString());
        Assert.Equal(1234, root.GetProperty("totalTokens").GetInt64());
        Assert.Equal(3, root.GetProperty("processedFiles").GetInt32());
        Assert.Equal(5, root.GetProperty("totalFiles").GetInt32());
        Assert.Equal(2, root.GetProperty("cacheHits").GetInt32());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.Equal("A.cs", root.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal("out_part1.txt", root.GetProperty("outputPaths")[0].GetString());
    }

    [Fact]
    public void Build_NamesTheTokenizer()
    {
        var result = new FusionResult([], null, 0, 0, 0, TimeSpan.Zero, []);
        var options = new EmissionOptions { TokenizerModel = "claude" };

        var json = RunReportBuilder.Build(result, options);

        Assert.Contains("\"tokenizer\":\"claude\"", json);
    }
}
