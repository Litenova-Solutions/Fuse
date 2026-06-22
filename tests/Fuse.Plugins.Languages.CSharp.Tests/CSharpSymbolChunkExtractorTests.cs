using Fuse.Plugins.Languages.CSharp.Outline;

namespace Fuse.Plugins.Languages.CSharp.Tests.Outline;

public class CSharpSymbolChunkExtractorTests
{
    private readonly CSharpSymbolChunkExtractor _extractor = new();

    [Fact]
    public void ExtractChunks_OneChunkPerMethod_WithParentAndBody()
    {
        const string input = """
            public class OrderService
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Sub(int a, int b) => a - b;
            }
            """;

        var chunks = _extractor.ExtractChunks(input);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Add", chunks[0].SymbolName);
        Assert.Equal("OrderService", chunks[0].ParentType);
        Assert.Equal("method", chunks[0].SymbolKind);
        Assert.Contains("return a + b;", chunks[0].Content);
        Assert.Equal("Sub", chunks[1].SymbolName);
        Assert.Contains("=> a - b;", chunks[1].Content);
    }

    [Fact]
    public void ExtractChunks_MemberBodiesParseStandalone()
    {
        const string input = """
            public class Calc
            {
                public int Compute(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++) { total += i; }
                    return total;
                }
            }
            """;

        var chunk = Assert.Single(_extractor.ExtractChunks(input));

        // A standalone member body must be brace-balanced so it parses on its own.
        Assert.Equal(CountChar(chunk.Content, '{'), CountChar(chunk.Content, '}'));
        Assert.StartsWith("public int Compute", chunk.Content);
        Assert.EndsWith("}", chunk.Content);
    }

    [Fact]
    public void ExtractChunks_RecordsLineSpans()
    {
        const string input = """
            public class Foo
            {
                public void Bar()
                {
                    Console.WriteLine();
                }
            }
            """;

        var chunk = Assert.Single(_extractor.ExtractChunks(input));

        // Bar spans lines 3 to 6 (1-based) in the source above.
        Assert.Equal(3, chunk.StartLine);
        Assert.Equal(6, chunk.EndLine);
    }

    [Fact]
    public void ExtractChunks_ConstructorKind()
    {
        const string input = """
            public class Widget
            {
                public Widget(int size)
                {
                    Size = size;
                }
            }
            """;

        var chunk = Assert.Single(_extractor.ExtractChunks(input));
        Assert.Equal("constructor", chunk.SymbolKind);
        Assert.Equal("Widget", chunk.SymbolName);
    }

    [Fact]
    public void ExtractChunks_IgnoresBracesInStringLiterals()
    {
        const string input = """
            public class Templater
            {
                public string Render() { return "{ not real braces }"; }
                public void After() { }
            }
            """;

        var chunks = _extractor.ExtractChunks(input);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Render", chunks[0].SymbolName);
        Assert.Equal("After", chunks[1].SymbolName);
    }

    [Fact]
    public void ExtractChunks_EnumMembers()
    {
        const string input = """
            public enum Status
            {
                Pending,
                Active = 2,
                Closed
            }
            """;

        var chunks = _extractor.ExtractChunks(input);

        Assert.Equal(new[] { "Pending", "Active", "Closed" }, chunks.Select(c => c.SymbolName));
        Assert.All(chunks, c => Assert.Equal("enum-member", c.SymbolKind));
    }

    [Fact]
    public void ExtractChunks_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(_extractor.ExtractChunks(string.Empty));
    }

    private static int CountChar(string text, char c) => text.Count(ch => ch == c);
}
