using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// R3: the signal classifier flags requests that carry no usable scoping signal (merge/dependency/CI noise,
// empty query with no structured input) and leaves solvable requests alone.
public sealed class QuerySignalClassifierTests
{
    [Theory]
    [InlineData("Merge branch 'main' into feature")]
    [InlineData("Merge pull request #1234 from acme/topic")]
    [InlineData("Apply suggestions from code review")]
    [InlineData("Bump Newtonsoft.Json from 12.0.1 to 13.0.1")]
    [InlineData("Upgrade to .NET 9")]
    [InlineData("ci: cache nuget packages")]
    [InlineData("")]
    [InlineData("   ")]
    public void FlagsLowSignalQueries(string query)
    {
        var verdict = QuerySignalClassifier.Classify(new LocalizationRequest(".", Query: query));

        Assert.True(verdict.IsLowSignal, $"expected low signal for '{query}'");
        Assert.False(string.IsNullOrWhiteSpace(verdict.Suggestion));
    }

    [Theory]
    [InlineData("Fix OrderService timeout on checkout")]
    [InlineData("discount calculation rounds incorrectly")]
    [InlineData("NullReferenceException in PaymentValidator")]
    public void DoesNotFlagSolvableQueries(string query)
    {
        var verdict = QuerySignalClassifier.Classify(new LocalizationRequest(".", Query: query));

        Assert.False(verdict.IsLowSignal, $"expected high signal for '{query}'");
        Assert.Null(verdict.Suggestion);
    }

    [Fact]
    public void StructuredSignalIsNeverLowSignalEvenWithNoQuery()
    {
        Assert.False(QuerySignalClassifier.Classify(new LocalizationRequest(".", Route: "GET /api/orders")).IsLowSignal);
        Assert.False(QuerySignalClassifier.Classify(new LocalizationRequest(".", Focus: "OrderService")).IsLowSignal);
        Assert.False(QuerySignalClassifier.Classify(new LocalizationRequest(".", ChangedSince: "origin/main")).IsLowSignal);
        // A merge-noise query is rescued by a structured signal.
        Assert.False(QuerySignalClassifier.Classify(
            new LocalizationRequest(".", Query: "Merge branch main", Service: "IOrderService")).IsLowSignal);
    }
}
