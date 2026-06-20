using System.Reflection;
using Fuse.Cli.Mcp;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Tests.Mcp;

public sealed class FuseToolsTests
{
    [Theory]
    [InlineData(nameof(FuseTools.FuseTocAsync), "fuse_toc")]
    [InlineData(nameof(FuseTools.FuseAskAsync), "fuse_ask")]
    [InlineData(nameof(FuseTools.FuseSkeletonAsync), "fuse_skeleton")]
    [InlineData(nameof(FuseTools.FuseFocusAsync), "fuse_focus")]
    [InlineData(nameof(FuseTools.FuseSearchAsync), "fuse_search")]
    [InlineData(nameof(FuseTools.FuseChangesAsync), "fuse_changes")]
    [InlineData(nameof(FuseTools.FuseDotNetAsync), "fuse_dotnet")]
    [InlineData(nameof(FuseTools.FuseGenericAsync), "fuse_generic")]
    public void ToolMethods_AreRegisteredWithExpectedNames(string methodName, string expectedToolName)
    {
        var method = typeof(FuseTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(expectedToolName, attribute!.Name);
    }

    [Theory]
    [InlineData(nameof(FuseResources.ReadSkeletonResourceAsync), "fuse://skeleton/{path}")]
    [InlineData(nameof(FuseResources.ReadFocusResourceAsync), "fuse://focus/{path}/{seed}")]
    [InlineData(nameof(FuseResources.ReadSearchResourceAsync), "fuse://search/{path}/{query}")]
    [InlineData(nameof(FuseResources.ReadChangesResourceAsync), "fuse://changes/{path}/{since}")]
    public void ResourceMethods_UseWorkflowUriTemplates(string methodName, string expectedTemplate)
    {
        var method = typeof(FuseResources).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<McpServerResourceAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(expectedTemplate, attribute!.UriTemplate);
    }

    [Fact]
    public async Task FuseTocAsync_EmitsTableOfContentsForDirectory()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Services/OrderService.cs", """
            public class OrderService
            {
                public void PlaceOrder() { }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseTocAsync(orchestrator, templates, fixture.Path);

        Assert.Contains("fuse:table-of-contents", result);
        Assert.Contains("OrderService.cs", result);
        Assert.Contains("class OrderService: PlaceOrder", result);
        Assert.DoesNotContain("<file path", result);
    }

    [Fact]
    public async Task FuseAskAsync_NamedType_FocusesAndAnnotatesStrategy()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Services/OrderService.cs", """
            public class OrderService
            {
                public void PlaceOrder() { }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseAskAsync(
            orchestrator, templates, fixture.Path, "How does OrderService place an order", tokenBudget: 20000);

        Assert.Contains("fuse_ask: strategy=focus", result);
        Assert.Contains("OrderService", result);
    }

    [Fact]
    public async Task FuseAskAsync_BroadQuestion_UsesSkeleton()
    {
        using var fixture = new TempProject();
        fixture.AddFile("Order.cs", """
            public class Order
            {
                public void Place() { var total = 1 + 2; }
            }
            """);

        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseAskAsync(
            orchestrator, templates, fixture.Path, "Give me an architecture overview", tokenBudget: 20000);

        Assert.Contains("fuse_ask: strategy=skeleton", result);
        // Skeleton keeps signatures but drops method bodies.
        Assert.DoesNotContain("var total = 1 + 2", result);
    }

    [Fact]
    public async Task FuseAskAsync_EmptyTask_ReturnsError()
    {
        var (orchestrator, templates) = BuildServices();
        var result = await FuseTools.FuseAskAsync(orchestrator, templates, Path.GetTempPath(), "  ");
        Assert.StartsWith("Error", result);
    }

    private static (FusionOrchestrator, ProjectTemplateRegistry) BuildServices()
    {
        var provider = new ServiceCollection().AddFuse().BuildServiceProvider();
        return (
            provider.GetRequiredService<FusionOrchestrator>(),
            provider.GetRequiredService<ProjectTemplateRegistry>());
    }

    private sealed class TempProject : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuse-mcp-tests", Guid.NewGuid().ToString("N"));

        public TempProject() => Directory.CreateDirectory(Path);

        public void AddFile(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
