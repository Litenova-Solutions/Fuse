using System.Text.Json;
using Fuse.BuildCaptureWorker;
using Fuse.Indexing;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// N4 tier-1: the worker emits the extracted graph bundle (symbols, nodes, edges, routes) as source-generated
// JSON on stdout, and the parent deserializes it to write to the store. This test pins the serialization
// contract: a bundle round-trips with its records intact, so the parent-side ingest reads what the worker wrote.
public sealed class CaptureBundleSerializationTests
{
    [Fact]
    public void Capture_result_round_trips_the_graph_bundle()
    {
        var project = new CapturedProject(
            Name: "App", FilePath: "/repo/App.csproj", AssemblyName: "App", ErrorCount: 0, TypeCount: 2,
            SymbolCount: 1, NodeCount: 2, EdgeCount: 1,
            Symbols: [new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "type", "OrderService",
                "App.OrderService", Accessibility: "public", Signature: "public sealed class OrderService", StartLine: 1, EndLine: 20, IsPublicApi: true)],
            Nodes:
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/OrderService.cs"),
                new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "src/OrderService.cs"),
            ],
            Edges: [new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95, Evidence: "registered scoped")],
            Routes: [new RouteRecord("route:POST:/api/orders", "POST", "/api/orders", "src/OrdersController.cs", 10, 12, "mvc")],
            DiRegistrations: [],
            OptionsBindings: []);
        var result = CaptureResult.Ok([project]);

        var json = JsonSerializer.Serialize(result, BuildCaptureJsonContext.Default.CaptureResult);
        var back = JsonSerializer.Deserialize(json, BuildCaptureJsonContext.Default.CaptureResult);

        Assert.NotNull(back);
        Assert.True(back!.Succeeded);
        var p = Assert.Single(back.Projects);
        Assert.Equal("App", p.Name);
        var symbol = Assert.Single(p.Symbols!);
        Assert.Equal("public sealed class OrderService", symbol.Signature);
        Assert.True(symbol.IsPublicApi);
        Assert.Equal(2, p.Nodes!.Count);
        var edge = Assert.Single(p.Edges!);
        Assert.Equal("di_resolves_to", edge.EdgeType);
        Assert.Equal(0.95, edge.Weight);
        var route = Assert.Single(p.Routes!);
        Assert.Equal("POST", route.HttpMethod);
    }
}
