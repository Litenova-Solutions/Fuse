using System.Reflection;
using Fuse.Cli.Mcp;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Tests.Mcp;

public sealed class FuseToolsTests
{
    [Theory]
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
}
