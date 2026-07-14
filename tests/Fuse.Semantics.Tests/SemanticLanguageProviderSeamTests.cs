using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// R11: the semantic-tier provider seam. A fake provider registered alongside C# is selected by language and
// contributes edges without touching the C# wiring graph; Suite A still runs through the C# provider unchanged.
public sealed class SemanticLanguageProviderSeamTests
{
    [Fact]
    public void RegistrySelectsProviderByLanguageAndBuildsRunners()
    {
        var registry = new SemanticLanguageProviderRegistry([new CSharpSemanticLanguageProvider(), new FakeSemanticLanguageProvider()]);

        Assert.Equal("csharp", registry.ForLanguage("csharp")!.Language);
        Assert.Equal("fake", registry.ForLanguage("fake")!.Language);
        Assert.Null(registry.ForLanguage("python"));
        Assert.Contains("csharp", registry.Languages);
        Assert.Contains("fake", registry.Languages);
    }

    [Fact]
    public void CSharpProviderProducesFixtureWiringEdgesUnchanged()
    {
        var result = new CSharpSemanticLanguageProvider().CreateRunner()
            .Run(new SemanticAnalysisContext(OrderingAppFixture.Load(), OrderingAppFixture.RootDirectory), CancellationToken.None);

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "di_resolves_to"
            && e.FromNodeId == "type:OrderingApp.Ordering.IOrderService"
            && e.ToNodeId == "type:OrderingApp.Ordering.OrderService");
    }

    [Fact]
    public void FakeProviderContributesEdgesWithoutTouchingCSharpPath()
    {
        var registry = new SemanticLanguageProviderRegistry([new CSharpSemanticLanguageProvider(), new FakeSemanticLanguageProvider()]);
        var csharp = registry.RunnerFor("csharp").Run(
            new SemanticAnalysisContext(OrderingAppFixture.Load(), OrderingAppFixture.RootDirectory),
            CancellationToken.None);
        var fake = registry.RunnerFor("fake").Run(
            new SemanticAnalysisContext(OrderingAppFixture.Load(), OrderingAppFixture.RootDirectory),
            CancellationToken.None);

        Assert.DoesNotContain(csharp.Edges, e => e.EdgeType == "fake_seam");
        Assert.Contains(fake.Edges, e =>
            e.EdgeType == "fake_seam"
            && e.FromNodeId == "node:fake-from"
            && e.ToNodeId == "node:fake-to");
        Assert.DoesNotContain(fake.Edges, e => e.EdgeType == "di_resolves_to");
    }

    [Fact]
    public void CreateDefaultStillDelegatesToCSharpProvider()
    {
        var fromDefault = SemanticAnalysisRunner.CreateDefault().Run(
            new SemanticAnalysisContext(OrderingAppFixture.Load(), OrderingAppFixture.RootDirectory),
            CancellationToken.None);
        var fromProvider = new CSharpSemanticLanguageProvider().CreateRunner().Run(
            new SemanticAnalysisContext(OrderingAppFixture.Load(), OrderingAppFixture.RootDirectory),
            CancellationToken.None);

        Assert.Equal(fromProvider.Edges.Count, fromDefault.Edges.Count);
        foreach (var edge in fromProvider.Edges)
            Assert.Contains(fromDefault.Edges, e => e.EdgeType == edge.EdgeType && e.FromNodeId == edge.FromNodeId && e.ToNodeId == edge.ToNodeId);
    }

    private sealed class FakeSemanticLanguageProvider : ISemanticLanguageProvider
    {
        public string Language => "fake";

        public IReadOnlyList<ISemanticAnalyzer> Analyzers { get; } = [new FakeEdgeAnalyzer()];
    }

    private sealed class FakeEdgeAnalyzer : ISemanticAnalyzer
    {
        public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken) =>
            SemanticAnalyzerResult.FromGraph(
            [
                new NodeRecord("node:fake-from", "fake", "FakeFrom", "fake-from"),
                new NodeRecord("node:fake-to", "fake", "FakeTo", "fake-to"),
            ],
            [
                new SemanticEdgeRecord("node:fake-from", "node:fake-to", "fake_seam", 1.0, 1.0),
            ]);
    }
}
