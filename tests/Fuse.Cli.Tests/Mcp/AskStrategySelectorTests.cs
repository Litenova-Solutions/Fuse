using Fuse.Cli.Mcp;

namespace Fuse.Cli.Tests.Mcp;

public sealed class AskStrategySelectorTests
{
    [Theory]
    [InlineData("Give me an overview of the architecture")]
    [InlineData("What does this project do at a high level")]
    [InlineData("Explain the overall structure")]
    public void Select_BroadQuestion_ChoosesSkeleton(string task)
    {
        var plan = AskStrategySelector.Select(task, 20000);
        Assert.Equal(AskMode.Skeleton, plan.Mode);
    }

    [Theory]
    [InlineData("How does OrderService charge a customer", "OrderService")]
    [InlineData("Find the bug in PaymentGateway", "PaymentGateway")]
    public void Select_NamesOneType_ChoosesFocusOnThatType(string task, string expectedSeed)
    {
        var plan = AskStrategySelector.Select(task, 20000);
        Assert.Equal(AskMode.Focus, plan.Mode);
        Assert.Equal(expectedSeed, plan.Seed);
    }

    [Fact]
    public void Select_NamesSeveralTypes_FallsBackToSearch()
    {
        var plan = AskStrategySelector.Select("How do OrderService and PaymentGateway interact", 20000);
        Assert.Equal(AskMode.Search, plan.Mode);
        Assert.Null(plan.Seed);
    }

    [Fact]
    public void Select_ConceptQuery_ChoosesSearch()
    {
        var plan = AskStrategySelector.Select("where is rate limiting handled", 20000);
        Assert.Equal(AskMode.Search, plan.Mode);
    }

    [Fact]
    public void Select_DoesNotTreatSentenceInitialWordAsType()
    {
        // "Which" begins the sentence but is not a compound identifier, so it must not be taken as a seed.
        var plan = AskStrategySelector.Select("Which file validates email addresses", 20000);
        Assert.Equal(AskMode.Search, plan.Mode);
    }

    [Theory]
    [InlineData(20000, 1)]
    [InlineData(60000, 2)]
    public void Select_LargerBudget_AllowsDeeperExpansion(int budget, int expectedDepth)
    {
        var plan = AskStrategySelector.Select("where is rate limiting handled", budget);
        Assert.Equal(expectedDepth, plan.Depth);
    }
}
