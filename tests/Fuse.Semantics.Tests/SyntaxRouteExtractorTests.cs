using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// P2.3: route extraction into records from controllers and minimal APIs.
public sealed class SyntaxRouteExtractorTests
{
    private readonly SyntaxRouteExtractor _extractor = new();

    [Fact]
    public void ExtractsControllerActionRouteWithPrefix()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            [Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpPost("{id}")]
                public IActionResult Create(int id) => Ok();
            }
            """;

        var routes = _extractor.Extract("src/OrdersController.cs", source);

        var route = Assert.Single(routes);
        Assert.Equal("POST", route.HttpMethod);
        Assert.Equal("/api/orders/{id}", route.RoutePattern);
        Assert.Equal("route:POST:/api/orders/{id}", route.RouteId);
        Assert.Equal("mvc", route.SourceKind);
        Assert.Contains("OrdersController.Create", route.MetadataJson);
    }

    [Fact]
    public void ExtractsMinimalApiRoute()
    {
        const string source = """
            var app = WebApplication.Create();
            app.MapGet("/health", () => "ok");
            """;

        var routes = _extractor.Extract("src/Program.cs", source);

        var route = Assert.Single(routes);
        Assert.Equal("GET", route.HttpMethod);
        Assert.Equal("/health", route.RoutePattern);
        Assert.Equal("minimal-api", route.SourceKind);
    }

    [Fact]
    public void ReturnsEmptyForNonRouteFile()
    {
        var routes = _extractor.Extract("src/Plain.cs", "namespace App; public class Plain { }");

        Assert.Empty(routes);
    }
}
