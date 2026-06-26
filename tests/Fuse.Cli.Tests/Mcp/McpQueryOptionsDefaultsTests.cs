using Fuse.Cli.Mcp;

namespace Fuse.Cli.Tests.Mcp;

public sealed class McpQueryOptionsDefaultsTests
{
    [Fact]
    public async Task FuseAskAsync_ConceptQuery_UsesSearchStrategy()
    {
        using var fixture = new FuseToolsTestHost.TempProject();
        fixture.AddFile("OrderService.cs", """
            public class OrderService
            {
                public void Charge() { }
            }
            """);

        var (orchestrator, templates) = FuseToolsTestHost.BuildServices();
        var result = await FuseTools.FuseAskAsync(
            orchestrator,
            templates,
            fixture.ProjectPath,
            "where is order charge payment handled",
            tokenBudget: 20000);

        Assert.Contains("fuse_ask: strategy=search", result);
        Assert.Contains("OrderService", result);
    }
}
