using System.Text.Json;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Emission.Tests;

public class TableOfContentsBuilderTests
{
    private static TocFileEntry Entry(string path, long tokens, params OutlineSymbol[] symbols) =>
        new(path, tokens, symbols);

    [Fact]
    public void Build_EmptyFiles_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TableOfContentsBuilder.Build([], OutputFormat.Xml));
    }

    [Fact]
    public void Build_Tree_RendersDirectoriesAndPerFileTokenCost()
    {
        var files = new[]
        {
            Entry("src/Services/OrderService.cs", 1200, new OutlineSymbol("class", "OrderService", ["PlaceOrder"])),
            Entry("src/Models/Order.cs", 300, new OutlineSymbol("class", "Order", [])),
        };

        var toc = TableOfContentsBuilder.Build(files, OutputFormat.Xml);

        Assert.Contains("fuse:table-of-contents files=2", toc);
        Assert.Contains("src/", toc);
        Assert.Contains("Services/", toc);
        Assert.Contains("OrderService.cs (~1.2k tokens)", toc);
        Assert.Contains("class OrderService: PlaceOrder", toc);
        Assert.Contains("Order.cs (~300 tokens)", toc);
    }

    [Fact]
    public void Build_Tree_DoesNotRepeatSharedDirectorySegments()
    {
        var files = new[]
        {
            Entry("src/A.cs", 10, new OutlineSymbol("class", "A", [])),
            Entry("src/B.cs", 10, new OutlineSymbol("class", "B", [])),
        };

        var toc = TableOfContentsBuilder.Build(files, OutputFormat.Xml);

        // The shared "src/" directory is printed once, not once per file.
        var occurrences = toc.Split("src/").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Build_Json_ProducesStructuredEntriesWithReadCost()
    {
        var files = new[]
        {
            Entry("src/A.cs", 100, new OutlineSymbol("class", "A", ["M"])),
            Entry("src/B.cs", 50),
        };

        var json = TableOfContentsBuilder.Build(files, OutputFormat.Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("files").GetInt32());
        Assert.Equal(150, root.GetProperty("readCostTokens").GetInt64());
        var entries = root.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("src/A.cs", entries[0].GetProperty("path").GetString());
        Assert.Equal("M", entries[0].GetProperty("symbols")[0].GetProperty("members")[0].GetString());
    }

    [Fact]
    public void Build_OrdersFilesByPath()
    {
        var files = new[]
        {
            Entry("zeta.cs", 10),
            Entry("alpha.cs", 10),
        };

        var toc = TableOfContentsBuilder.Build(files, OutputFormat.Xml);

        Assert.True(toc.IndexOf("alpha.cs", StringComparison.Ordinal) < toc.IndexOf("zeta.cs", StringComparison.Ordinal));
    }
}
