using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class FtsSearchEngineUnitTests
{
    [Fact]
    public void BuildMatchExpression_quotes_terms_and_or_joins()
    {
        var match = FtsSearchEngine.BuildMatchExpression("OrderService invoice");

        Assert.Contains("\"OrderService\"", match);
        Assert.Contains("\"invoice\"", match);
        Assert.Contains(" OR ", match);
    }

    [Fact]
    public void BuildMatchExpression_expands_subwords_for_compound_identifiers()
    {
        var match = FtsSearchEngine.BuildMatchExpression("rounding");

        Assert.Contains("\"rounding\"", match);
    }

    [Fact]
    public void BuildMatchExpression_returns_empty_for_whitespace_only()
    {
        Assert.Equal(string.Empty, FtsSearchEngine.BuildMatchExpression("   "));
    }
}
