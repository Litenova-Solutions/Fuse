using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// S2: the comment extractor pulls human-written prose out of source so it can be indexed as a weighted field.
public sealed class CommentExtractorTests
{
    [Fact]
    public void ExtractsLineComment()
    {
        Assert.Equal("computes the discount", CommentExtractor.Extract("// computes the discount\npublic void Foo() {}"));
    }

    [Fact]
    public void ExtractsXmlDocCommentWithTagsStripped()
    {
        var extracted = CommentExtractor.Extract("/// <summary>Calculates the rounding</summary>\npublic decimal Round() => 0;");

        Assert.Contains("Calculates the rounding", extracted);
        Assert.DoesNotContain("<summary>", extracted);
    }

    [Fact]
    public void ExtractsBlockComment()
    {
        Assert.Equal("block comment here", CommentExtractor.Extract("/* block comment here */ var x = 1;"));
    }

    [Fact]
    public void ExtractsHashComment()
    {
        Assert.Equal("a python style comment", CommentExtractor.Extract("# a python style comment\nx = 1"));
    }

    [Fact]
    public void ReturnsEmptyWhenNoComments()
    {
        Assert.Equal(string.Empty, CommentExtractor.Extract("public void Foo() { return; }"));
        Assert.Equal(string.Empty, CommentExtractor.Extract(null));
    }
}
