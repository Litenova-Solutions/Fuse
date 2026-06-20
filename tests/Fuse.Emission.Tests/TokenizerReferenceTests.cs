using Fuse.Emission.Tokenization;

namespace Fuse.Emission.Tests;

public sealed class TokenizerReferenceTests
{
    public static TheoryData<string, string, int> O200kReferenceVectors =>
        new()
        {
            { "o200k_base", "", 0 },
            { "o200k_base", "hello world", 2 },
            { "o200k_base", "日本語", 2 },
            { "o200k_base", "public class Foo { }", 5 },
            { "o200k_base", "# Title\n\nParagraph.", 5 },
        };

    public static TheoryData<string, string, int> Cl100kReferenceVectors =>
        new()
        {
            { "cl100k_base", "", 0 },
            { "cl100k_base", "hello world", 2 },
            { "cl100k_base", "日本語", 4 },
            { "cl100k_base", "public class Foo { }", 5 },
            { "cl100k_base", "# Title\n\nParagraph.", 5 },
        };

    [Theory]
    [MemberData(nameof(O200kReferenceVectors))]
    [MemberData(nameof(Cl100kReferenceVectors))]
    public void Count_MatchesReferenceVector(string encoding, string input, int expected)
    {
        var counter = new TikTokenCounter(encoding);
        Assert.Equal(expected, counter.Count(input));
    }

    [Fact]
    public void Count_CalledTwice_ReturnsIdenticalValue()
    {
        const string input = "public async Task RunAsync() { await Task.CompletedTask; }";
        var counter = new TikTokenCounter("o200k_base");

        var first = counter.Count(input);
        var second = counter.Count(input);

        Assert.Equal(first, second);
        Assert.True(first > 0);
    }

    [Fact]
    public void GetCounter_SameEncoding_ReturnsCachedInstance()
    {
        var factory = new TokenizerFactory();
        var first = factory.GetCounter("o200k_base");
        var second = factory.GetCounter("gpt-4o");

        Assert.Same(first, second);
    }

    [Fact]
    public void GetCounter_DifferentEncodings_ReturnsDistinctInstances()
    {
        var factory = new TokenizerFactory();
        var o200k = factory.GetCounter("o200k_base");
        var cl100k = factory.GetCounter("cl100k_base");

        Assert.NotSame(o200k, cl100k);
        Assert.NotEqual(o200k.Count("日本語"), cl100k.Count("日本語"));
    }

    [Theory]
    [InlineData("gpt-4o", "o200k_base")]
    [InlineData("gpt-4", "cl100k_base")]
    public void ResolveEncoding_MapsModelNames(string model, string expectedEncoding)
    {
        Assert.Equal(expectedEncoding, TokenizerFactory.ResolveEncoding(model));
        Assert.Equal(
            new TikTokenCounter(expectedEncoding).Count("hello"),
            new TikTokenCounter(model).Count("hello"));
    }
}
