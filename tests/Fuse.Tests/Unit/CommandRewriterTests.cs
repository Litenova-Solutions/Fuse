using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class CommandRewriterTests
{
    [Theory]
    [InlineData("dotnet build", "fuse build")]
    [InlineData("dotnet test", "fuse test")]
    [InlineData("dotnet test --filter Foo", "fuse test --filter Foo")]
    [InlineData("cd src && dotnet build -c Release", "cd src && fuse build -c Release")]
    [InlineData("dotnet build; dotnet test", "fuse build; fuse test")]
    [InlineData("dotnet test | tail -20", "fuse test | tail -20")]
    [InlineData("false || dotnet test", "false || fuse test")]
    [InlineData("  dotnet.exe test", "  fuse test")]
    public void Rewrites_dotnet_build_and_test_at_segment_starts(string command, string expected)
    {
        Assert.Equal(expected, CommandRewriter.Rewrite(command));
    }

    [Theory]
    [InlineData("dotnet run")]
    [InlineData("dotnet builder")]
    [InlineData("dotnet testx")]
    [InlineData("dotnet publish")]
    [InlineData("git commit -m \"run dotnet\"")]
    [InlineData("mydotnet test")]
    public void Leaves_other_commands_alone(string command)
    {
        Assert.Null(CommandRewriter.Rewrite(command));
    }

    [Fact]
    public void Text_after_echo_is_not_a_segment_start()
    {
        // "echo dotnet test" starts with echo, so nothing is rewritten. A quoted "a; dotnet test" inside an argument
        // would be rewritten, because the rewriter does not parse shell quoting; that only changes the echoed text.
        Assert.Null(CommandRewriter.Rewrite("echo dotnet test"));
    }
}
