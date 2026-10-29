using System.Text.Json;
using Fuse.Mcp;

namespace Fuse.Tests.Unit;

/// <summary>The tool list every MCP host reads first: three tools, each with an object schema that parses.</summary>
public class McpToolListTests
{
    [Fact]
    public void Every_tool_has_an_object_schema()
    {
        Assert.Equal(["fuse_check", "fuse_test", "fuse_build"], McpCommand.Tools.Select(t => t.Name));
        foreach (var tool in McpCommand.Tools)
        {
            Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        }
    }
}
