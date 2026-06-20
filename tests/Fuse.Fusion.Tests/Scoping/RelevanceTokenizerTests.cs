using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class RelevanceTokenizerTests
{
    [Fact]
    public void Tokenize_SplitsCamelCaseAndKeepsWhole()
    {
        var terms = RelevanceTokenizer.Tokenize("OrderService");
        Assert.Contains("orderservice", terms);
        Assert.Contains("order", terms);
        Assert.Contains("service", terms);
    }

    [Fact]
    public void Tokenize_DropsStopwords()
    {
        var terms = RelevanceTokenizer.Tokenize("which file contains the order processor");
        Assert.DoesNotContain("which", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("file", terms);
        Assert.DoesNotContain("contains", terms);
        Assert.Contains("order", terms);
    }

    [Theory]
    [InlineData("validators", "validator")]
    [InlineData("processing", "process")]
    [InlineData("handlers", "handler")]
    [InlineData("categories", "category")]
    [InlineData("mapping", "mapp")]
    public void Stem_FoldsCommonSuffixes(string input, string expected)
    {
        Assert.Equal(expected, RelevanceTokenizer.Stem(input));
    }

    [Fact]
    public void Stem_KeepsShortAndDoubleS()
    {
        Assert.Equal("ids", RelevanceTokenizer.Stem("ids")); // too short to strip
        Assert.Equal("process", RelevanceTokenizer.Stem("process")); // ends with ss, kept
    }

    [Fact]
    public void Tokenize_PluralAndSingularConverge()
    {
        var plural = RelevanceTokenizer.Tokenize("validators");
        var singular = RelevanceTokenizer.Tokenize("validator");
        Assert.Equal(singular, plural);
    }

    [Fact]
    public void Tokenize_VerbFormsConverge()
    {
        var gerund = RelevanceTokenizer.Tokenize("mapping");
        var past = RelevanceTokenizer.Tokenize("mapped");
        Assert.Equal(gerund, past);
    }
}
