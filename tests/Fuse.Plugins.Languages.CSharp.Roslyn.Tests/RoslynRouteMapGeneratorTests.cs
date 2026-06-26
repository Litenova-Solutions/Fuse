using Fuse.Plugins.Languages.CSharp.Roslyn.Maps;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Tests;

public sealed class RoslynRouteMapGeneratorTests
{
    private readonly RoslynRouteMapGenerator _generator = new();

    [Fact]
    public void Generate_ControllerAction_ProducesRouteRow()
    {
        var content = new Dictionary<string, string>
        {
            ["Controllers/OrdersController.cs"] = """
                [Route("api/[controller]")]
                public class OrdersController
                {
                    [HttpGet("{id}")]
                    public IActionResult Get(int id) => Ok();
                }
                """
        };

        var result = _generator.Generate(content);

        Assert.Contains("fuse:route-map", result);
        Assert.Contains("GET", result);
        Assert.Contains("api/[controller]/{id}", result);
        Assert.Contains("Get", result);
    }

    [Fact]
    public void Generate_MinimalApi_ProducesRouteRow()
    {
        var content = new Dictionary<string, string>
        {
            ["Program.cs"] = """app.MapPost("/hooks/github", () => Results.Ok());"""
        };

        var result = _generator.Generate(content);

        Assert.Contains("POST", result);
        Assert.Contains("/hooks/github", result);
        Assert.Contains("minimal-api", result);
    }

    [Fact]
    public void Generate_VerbOnlyAction_UsesHandlerNameAsRoute()
    {
        var content = new Dictionary<string, string>
        {
            ["Controllers/ItemsController.cs"] = """
                [Route("api/items")]
                public class ItemsController
                {
                    [HttpDelete]
                    public IActionResult Remove(int id) => Ok();
                }
                """
        };

        var result = _generator.Generate(content);

        Assert.Contains("DELETE", result);
        Assert.Contains("api/items/Remove", result);
        Assert.Contains("Remove", result);
    }

    [Fact]
    public void Generate_RouteAttributeOnAction_DefaultsToGet()
    {
        var content = new Dictionary<string, string>
        {
            ["Controllers/HealthController.cs"] = """
                public class HealthController
                {
                    [Route("health")]
                    public IActionResult Ping() => Ok();
                }
                """
        };

        var result = _generator.Generate(content);

        Assert.Contains("GET", result);
        Assert.Contains("health", result);
        Assert.Contains("Ping", result);
    }

    [Fact]
    public void Generate_AllHttpVerbs_AreMapped()
    {
        var content = new Dictionary<string, string>
        {
            ["Program.cs"] = """
                app.MapGet("/g", () => {});
                app.MapPost("/p", () => {});
                app.MapPut("/u", () => {});
                app.MapDelete("/d", () => {});
                app.MapPatch("/x", () => {});
                """
        };

        var result = _generator.Generate(content);

        Assert.Contains("GET", result);
        Assert.Contains("POST", result);
        Assert.Contains("PUT", result);
        Assert.Contains("DELETE", result);
        Assert.Contains("PATCH", result);
    }

    [Fact]
    public void Generate_NonCsFile_IsSkipped()
    {
        var content = new Dictionary<string, string>
        {
            ["readme.txt"] = """app.MapGet("/ignored", () => {});"""
        };

        Assert.Equal(string.Empty, _generator.Generate(content));
    }

    [Fact]
    public void Generate_InvalidCSharp_ReturnsEmpty()
    {
        var content = new Dictionary<string, string>
        {
            ["Broken.cs"] = "{{{{ not csharp"
        };

        Assert.Equal(string.Empty, _generator.Generate(content));
    }
}
