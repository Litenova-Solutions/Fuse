using Fuse.Emission.Tokenization;

namespace Fuse.Emission.Tests;

public sealed class ApproximateTokenCounterTests
{
    [Fact]
    public void Count_EmptyContent_ReturnsZero()
    {
        var counter = new ApproximateTokenCounter(3.5);
        Assert.Equal(0, counter.Count(string.Empty));
    }

    [Fact]
    public void Count_IsDeterministic()
    {
        var counter = new ApproximateTokenCounter(4.0);
        const string input = "public class PaymentService { }";
        Assert.Equal(counter.Count(input), counter.Count(input));
    }

    [Fact]
    public void Count_SmallerCharsPerToken_YieldsMoreTokens()
    {
        const string input = "the quick brown fox jumps over the lazy dog";
        var fine = new ApproximateTokenCounter(2.0).Count(input);
        var coarse = new ApproximateTokenCounter(6.0).Count(input);
        Assert.True(fine > coarse);
    }

    [Fact]
    public void Count_CountsPunctuationAsTokens()
    {
        // "a" -> 1 word token; each of '.', '.', '.' -> 1 token; whitespace is free.
        var counter = new ApproximateTokenCounter(4.0);
        Assert.Equal(4, counter.Count("a . . ."));
    }

    [Fact]
    public void Constructor_NonPositiveRatio_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApproximateTokenCounter(0));
    }

    [Theory]
    [InlineData("claude", "anthropic")]
    [InlineData("claude-opus-4", "anthropic")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("gemini", "gemini")]
    [InlineData("gemini-1.5-pro", "gemini")]
    [InlineData("google", "gemini")]
    [InlineData("gpt-4o", "o200k_base")]
    [InlineData("cl100k_base", "cl100k_base")]
    public void ResolveFamily_MapsModelNames(string model, string expected)
    {
        Assert.Equal(expected, TokenizerFactory.ResolveFamily(model));
    }

    [Fact]
    public void GetCounter_AnthropicAliases_ReturnCachedApproximateCounter()
    {
        var factory = new TokenizerFactory();
        var a = factory.GetCounter("claude");
        var b = factory.GetCounter("claude-opus-4");

        Assert.Same(a, b);
        Assert.IsType<ApproximateTokenCounter>(a);
    }

    [Fact]
    public void GetCounter_AnthropicAndGemini_AreDistinct()
    {
        var factory = new TokenizerFactory();
        var anthropic = factory.GetCounter("claude");
        var gemini = factory.GetCounter("gemini");

        Assert.NotSame(anthropic, gemini);
        // Gemini uses a higher chars-per-token ratio, so it estimates fewer tokens than Anthropic.
        const string input = "public class OrderService { public void PlaceOrder() { } }";
        Assert.True(gemini.Count(input) <= anthropic.Count(input));
    }
}
