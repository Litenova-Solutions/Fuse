using Fuse.Cli.Commands;
using Fuse.Cli.Services;

namespace Fuse.Cli.Tests;

// R13 / R21: agent-first defaults for mcp serve without client env tuning.
public sealed class AgentDefaultsTests
{
    [Fact]
    public void McpServe_DaemonEnabled_ByDefault()
    {
        using var daemon = new EnvironmentVariableScope("FUSE_DAEMON", null);
        Assert.True(McpServeCommand.IsDaemonEnabled());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    public void McpServe_DaemonDisabled_WhenExplicitlyOptedOut(string value)
    {
        using var daemon = new EnvironmentVariableScope("FUSE_DAEMON", value);
        Assert.False(McpServeCommand.IsDaemonEnabled());
    }

    [Fact]
    public void FuseUpdatePrompt_AutoUpdateEnabled_ByDefaultForMcpServe()
    {
        using var autoUpdate = new EnvironmentVariableScope(FuseUpdatePrompt.AutoUpdateEnvironmentVariable, null);
        Assert.True(FuseUpdatePrompt.IsAutoUpdateEnabled(allowAutoUpdate: true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    public void FuseUpdatePrompt_AutoUpdateDisabled_WhenExplicitlyOptedOut(string value)
    {
        using var autoUpdate = new EnvironmentVariableScope(FuseUpdatePrompt.AutoUpdateEnvironmentVariable, value);
        Assert.False(FuseUpdatePrompt.IsAutoUpdateEnabled(allowAutoUpdate: true));
    }

    [Fact]
    public void FuseUpdatePrompt_AutoUpdateDisabled_ForCliEntryPoints()
        => Assert.False(FuseUpdatePrompt.IsAutoUpdateEnabled(allowAutoUpdate: false));

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
