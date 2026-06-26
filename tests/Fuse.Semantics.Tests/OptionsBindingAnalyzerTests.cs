using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.7: options binding and consumption edges over the OrderingApp fixture.
public sealed class OptionsBindingAnalyzerTests
{
    [Fact]
    public void EmitsBindsEdgeFromConfigSectionToOptions()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "options_binds"
            && e.FromNodeId == "config:Orders"
            && e.ToNodeId == "type:OrderingApp.Ordering.OrderOptions"
            && e.Weight == 0.85);
    }

    [Fact]
    public void EmitsConsumesEdgeFromServiceToOptions()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "options_consumes"
            && e.FromNodeId == "type:OrderingApp.Ordering.OrderService"
            && e.ToNodeId == "type:OrderingApp.Ordering.OrderOptions"
            && e.Weight == 0.75);
    }

    [Fact]
    public void RecordsBindingAndConfigNode()
    {
        var result = Analyze();

        var binding = Assert.Single(result.OptionsBindings, b => b.OptionsName == "OrderingApp.Ordering.OrderOptions");
        Assert.Equal("Orders", binding.ConfigSection);
        Assert.Equal("configure", binding.BindingKind);

        // The "Orders" section is indexed as a config node (from the bind call and appsettings.json).
        Assert.Contains(result.Nodes, n => n.NodeId == "config:Orders" && n.Kind == "config");
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return new OptionsBindingAnalyzer().Analyze(context, CancellationToken.None);
    }
}
