using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class HeuristicQueryRewriterTests
{
    [Fact]
    public void Rewrite_EmphasizesPascalCaseIdentifierTokensOverPlainWords()
    {
        // "PaymentService" is a compound identifier; its tokens outweigh the plain word "fix".
        var weights = HeuristicQueryRewriter.Rewrite("fix the PaymentService");

        Assert.True(weights["payment"] > weights["fix"]);
        Assert.True(weights["service"] > weights["fix"]);
        Assert.Equal(1.0, weights["fix"]);
    }

    [Fact]
    public void Rewrite_NoIdentifier_AllTermsEqualWeight()
    {
        var weights = HeuristicQueryRewriter.Rewrite("where is payment processed");

        Assert.NotEmpty(weights);
        Assert.All(weights.Values, w => Assert.Equal(1.0, w));
    }

    [Fact]
    public void Rewrite_Empty_ReturnsEmpty()
    {
        Assert.Empty(HeuristicQueryRewriter.Rewrite(""));
        Assert.Empty(HeuristicQueryRewriter.Rewrite("   "));
    }
}
