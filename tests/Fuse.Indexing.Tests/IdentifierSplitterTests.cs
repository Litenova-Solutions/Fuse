using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// S1: the identifier splitter expands compound names into subword parts at index and query time.
public sealed class IdentifierSplitterTests
{
    [Fact]
    public void SplitsCamelCase()
    {
        Assert.Equal(["apply", "rounding", "mode"], IdentifierSplitter.Split("ApplyRoundingMode"));
    }

    [Fact]
    public void SplitsSnakeCase()
    {
        Assert.Equal(["order", "total"], IdentifierSplitter.Split("order_total"));
    }

    [Fact]
    public void SplitsAcronymThenWord()
    {
        Assert.Equal(["xml", "parser"], IdentifierSplitter.Split("XMLParser"));
        Assert.Equal(["http", "request", "handler"], IdentifierSplitter.Split("HTTPRequestHandler"));
    }

    [Fact]
    public void SplitsLetterDigitBoundary()
    {
        Assert.Equal(["base", "64", "encode"], IdentifierSplitter.Split("base64Encode"));
    }

    [Fact]
    public void DropsRunsShorterThanMinLength()
    {
        // The lone "x" run is below the minimum length and dropped as noise.
        Assert.Equal(["handler"], IdentifierSplitter.Split("xHandler"));
    }

    [Fact]
    public void SingleWordReturnsItselfLowercased()
    {
        Assert.Equal(["rounding"], IdentifierSplitter.Split("rounding"));
    }

    [Fact]
    public void ExpandDedupesAcrossFields()
    {
        // "OrderService" and "IOrderService" share the "order" and "service" parts; the expansion keeps each once.
        var expanded = IdentifierSplitter.Expand("OrderService", "IOrderService", "void Place(Order order)");

        var parts = expanded.Split(' ');
        Assert.Equal(parts.Distinct().Count(), parts.Length);
        Assert.Contains("order", parts);
        Assert.Contains("service", parts);
        Assert.Contains("place", parts);
    }

    [Fact]
    public void EmptyInputReturnsEmpty()
    {
        Assert.Empty(IdentifierSplitter.Split(null));
        Assert.Empty(IdentifierSplitter.Split(""));
        Assert.Equal(string.Empty, IdentifierSplitter.Expand(null, ""));
    }
}
