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

    [Theory]
    // Whole token is always kept; the listed sub-words must all appear (after stemming).
    [InlineData("HTTPClientFactory", new[] { "http", "client", "factory" })]
    [InlineData("IPAddress", new[] { "ip", "address" })]
    [InlineData("OAuth2Token", new[] { "auth", "2", "token" })]
    [InlineData("XMLReader", new[] { "xml", "reader" })]
    [InlineData("base64Url", new[] { "base", "64", "url" })]
    [InlineData("snake_case_name", new[] { "snake", "case", "name" })]
    public void Tokenize_SplitsAcronymDigitAndDelimiterBoundaries(string input, string[] expectedSubwords)
    {
        var terms = RelevanceTokenizer.Tokenize(input);
        foreach (var sub in expectedSubwords)
            Assert.Contains(sub, terms);
    }

    [Theory]
    [InlineData("Json.NET", new[] { "json", "net" })]
    [InlineData("kebab-case", new[] { "kebab", "case" })]
    public void Tokenize_SplitsOnNonWordPunctuation(string input, string[] expected)
    {
        var terms = RelevanceTokenizer.Tokenize(input);
        foreach (var e in expected)
            Assert.Contains(e, terms);
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
