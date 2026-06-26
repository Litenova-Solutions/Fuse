using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// R5: the wider semantic analyzer set (hosted services, MediatR pipeline behaviors, EF Core) over the
// OrderingApp fixture, which exercises each new wiring kind.
public sealed class WiringAnalyzersTests
{
    [Fact]
    public void HostedService_emits_edge_from_contract_to_worker()
    {
        var result = Analyze(new HostedServiceAnalyzer());

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "hosted_service"
            && e.FromNodeId == "service:IHostedService"
            && e.ToNodeId == "type:OrderingApp.Ordering.OrderDispatcher");
        Assert.Contains(result.Nodes, n => n.NodeId == "service:IHostedService");
    }

    [Fact]
    public void PipelineBehavior_emits_edge_from_contract_to_behavior()
    {
        var result = Analyze(new PipelineBehaviorAnalyzer());

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "pipeline_behavior"
            && e.FromNodeId == "service:IPipelineBehavior"
            && e.ToNodeId == "type:OrderingApp.Ordering.LoggingBehavior");
    }

    [Fact]
    public void EfCore_emits_dbcontext_to_entity_and_entity_to_configuration()
    {
        var result = Analyze(new EfCoreAnalyzer());

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "ef_entity"
            && e.FromNodeId == "type:OrderingApp.Data.OrderingDbContext"
            && e.ToNodeId == "type:OrderingApp.Data.OrderEntity");
        Assert.Contains(result.Edges, e =>
            e.EdgeType == "ef_configures"
            && e.FromNodeId == "type:OrderingApp.Data.OrderEntity"
            && e.ToNodeId == "type:OrderingApp.Data.OrderEntityConfiguration");
    }

    private static SemanticAnalyzerResult Analyze(ISemanticAnalyzer analyzer)
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return analyzer.Analyze(context, CancellationToken.None);
    }
}
