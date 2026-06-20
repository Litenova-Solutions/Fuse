using Fuse.Plugins.Languages.CSharp.Maps;

namespace Fuse.Plugins.Languages.CSharp.Tests.Maps;

public sealed class CSharpRouteMapGeneratorTests
{
    private readonly CSharpRouteMapGenerator _generator = new();

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
}
