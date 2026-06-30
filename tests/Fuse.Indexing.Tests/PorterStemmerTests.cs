using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// S2: the Porter stemmer collapses inflected variants to a common stem so a query word matches an inflected
// indexed word. The expected values are the canonical Porter (1980) outputs.
public sealed class PorterStemmerTests
{
    [Theory]
    [InlineData("rounding", "round")]
    [InlineData("rounds", "round")]
    [InlineData("rounded", "round")]
    [InlineData("calculate", "calcul")]
    [InlineData("calculation", "calcul")]
    [InlineData("validate", "valid")]
    [InlineData("validation", "valid")]
    [InlineData("caresses", "caress")]
    [InlineData("ponies", "poni")]
    [InlineData("cats", "cat")]
    [InlineData("happy", "happi")]
    [InlineData("relational", "relat")]
    [InlineData("plastered", "plaster")]
    [InlineData("motoring", "motor")]
    public void StemsToCanonicalForm(string word, string expected)
    {
        Assert.Equal(expected, PorterStemmer.Stem(word));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("be")]
    public void ShortWordsAreUnchanged(string word)
    {
        Assert.Equal(word, PorterStemmer.Stem(word));
    }

    [Fact]
    public void NonAlphabeticTokensAreLowercasedButNotStemmed()
    {
        Assert.Equal("base64", PorterStemmer.Stem("base64"));
        Assert.Equal("http2", PorterStemmer.Stem("HTTP2"));
    }

    [Fact]
    public void InflectionsCollapseToTheSameStem()
    {
        var rounding = PorterStemmer.Stem("rounding");
        Assert.Equal(rounding, PorterStemmer.Stem("rounds"));
        Assert.Equal(rounding, PorterStemmer.Stem("rounded"));
    }

    [Fact]
    public void ExpandStemsIdentifierSubwordsAndComments()
    {
        // The expansion splits identifiers into subwords, then stems each, deduped.
        var expanded = PorterStemmer.Expand("ApplyRoundingMode", "calculates the totals");

        var parts = expanded.Split(' ');
        Assert.Contains("round", parts);   // from Rounding
        Assert.Contains("calcul", parts);  // from calculates
        Assert.Contains("total", parts);   // from totals
        Assert.Equal(parts.Distinct().Count(), parts.Length);
    }
}
