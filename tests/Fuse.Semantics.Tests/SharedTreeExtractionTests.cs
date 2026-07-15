using Fuse.Semantics;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fuse.Semantics.Tests;

// R47: the semantic index pipeline parses each file once and shares the syntax tree between the chunk (symbol) and
// route extractors. This proves the shared-tree overloads produce byte-identical records to the content overloads,
// so sharing one parse across both extractors changes the parse count, never the output.
public sealed class SharedTreeExtractionTests
{
    private const string Source = """
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;

        [Route("api/orders")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{id}")]
            public IActionResult Get(int id) => Ok(id);

            public int Helper(int x) => x + 1;
        }

        public sealed record Widget(string Name)
        {
            public string Describe() => Name;
        }
        """;

    [Fact]
    public void Symbol_extraction_from_a_shared_tree_matches_the_content_overload()
    {
        var extractor = new SyntaxSymbolExtractor();
        var fromContent = extractor.Extract("Demo/OrdersController.cs", Source);
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();
        var fromTree = extractor.Extract("Demo/OrdersController.cs", root);

        Assert.Equal(fromContent.Symbols.Count, fromTree.Symbols.Count);
        Assert.Equal(fromContent.Chunks.Count, fromTree.Chunks.Count);
        for (var i = 0; i < fromContent.Symbols.Count; i++)
        {
            Assert.Equal(fromContent.Symbols[i].SymbolId, fromTree.Symbols[i].SymbolId);
            Assert.Equal(fromContent.Symbols[i].Signature, fromTree.Symbols[i].Signature);
            Assert.Equal(fromContent.Symbols[i].StartLine, fromTree.Symbols[i].StartLine);
        }

        for (var i = 0; i < fromContent.Chunks.Count; i++)
        {
            Assert.Equal(fromContent.Chunks[i].ChunkId, fromTree.Chunks[i].ChunkId);
            Assert.Equal(fromContent.Chunks[i].StableKey, fromTree.Chunks[i].StableKey);
            Assert.Equal(fromContent.Chunks[i].TextHash, fromTree.Chunks[i].TextHash);
        }
    }

    [Fact]
    public void Route_extraction_from_a_shared_tree_matches_the_content_overload()
    {
        var extractor = new SyntaxRouteExtractor();
        var fromContent = extractor.Extract("Demo/OrdersController.cs", Source);
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();
        var fromTree = extractor.Extract("Demo/OrdersController.cs", root);

        Assert.NotEmpty(fromContent); // The controller declares a route, so this is a meaningful comparison.
        Assert.Equal(fromContent.Count, fromTree.Count);
        for (var i = 0; i < fromContent.Count; i++)
        {
            Assert.Equal(fromContent[i].RouteId, fromTree[i].RouteId);
            Assert.Equal(fromContent[i].HttpMethod, fromTree[i].HttpMethod);
            Assert.Equal(fromContent[i].RoutePattern, fromTree[i].RoutePattern);
            Assert.Equal(fromContent[i].HandlerSymbolId, fromTree[i].HandlerSymbolId);
        }
    }
}
