using Fuse.Hooks;

namespace Fuse.Tests.Unit;

public class CommandRewriterTests
{
    [Theory]
    [InlineData("dotnet build", "fuse build --from-rewrite")]
    [InlineData("dotnet test", "fuse test --from-rewrite")]
    [InlineData("dotnet test --filter Foo", "fuse test --from-rewrite --filter Foo")]
    [InlineData("cd src && dotnet build -c Release", "cd src && fuse build --from-rewrite -c Release")]
    [InlineData("dotnet build; dotnet test", "fuse build --from-rewrite; fuse test --from-rewrite")]
    [InlineData("dotnet test | tail -20", "fuse test --from-rewrite | tail -20")]
    [InlineData("false || dotnet test", "false || fuse test --from-rewrite")]
    [InlineData("  dotnet.exe test", "  fuse test --from-rewrite")]
    [InlineData("DOTNET_CLI_UI_LANGUAGE=en dotnet test", "DOTNET_CLI_UI_LANGUAGE=en fuse test --from-rewrite")]
    [InlineData("A=1 B_2=x/y dotnet build -c Release", "A=1 B_2=x/y fuse build --from-rewrite -c Release")]
    [InlineData("cd src && CI= dotnet test | tail", "cd src && CI= fuse test --from-rewrite | tail")]
    [InlineData("X=$HOME dotnet test", "X=$HOME fuse test --from-rewrite")]
    public void Rewrites_dotnet_build_and_test_at_segment_starts(string command, string expected)
    {
        Assert.Equal(expected, CommandRewriter.Rewrite(command));
    }

    [Theory]
    [InlineData("dotnet test", "fuse test --from-rewrite --session claude:a1/agent-2")]
    [InlineData("dotnet test --filter Foo", "fuse test --from-rewrite --session claude:a1/agent-2 --filter Foo")]
    [InlineData("dotnet build && dotnet test", "fuse build --from-rewrite && fuse test --from-rewrite --session claude:a1/agent-2")]
    public void Passes_the_session_to_fuse_test_only(string command, string expected)
    {
        Assert.Equal(expected, CommandRewriter.Rewrite(command, "claude:a1/agent-2"));
        Assert.Equal(expected.Contains("&&", StringComparison.Ordinal) ? null : expected, CommandRewriter.RewriteWhole(command, "claude:a1/agent-2"));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a;rm -rf x")]
    [InlineData("$(id)")]
    [InlineData("a'b")]
    public void Leaves_out_a_session_id_a_shell_would_not_pass_on_unchanged(string session)
    {
        Assert.Equal("fuse test --from-rewrite", CommandRewriter.Rewrite("dotnet test", session));
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

    [Theory]
    [InlineData("dotnet build", "fuse build --from-rewrite")]
    [InlineData("dotnet test", "fuse test --from-rewrite")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName~Lib.Tests.CalcTests", "fuse test --from-rewrite --no-build --filter FullyQualifiedName~Lib.Tests.CalcTests")]
    [InlineData("  dotnet.exe build src\\App\\App.csproj -c Release -p:Version=1.2.3  ", "  fuse build --from-rewrite src\\App\\App.csproj -c Release -p:Version=1.2.3  ")]
    [InlineData("DOTNET_CLI_UI_LANGUAGE=en dotnet test --no-build", "DOTNET_CLI_UI_LANGUAGE=en fuse test --from-rewrite --no-build")]
    public void A_command_that_is_one_plain_invocation_is_rewritten_whole(string command, string expected)
    {
        Assert.Equal(expected, CommandRewriter.RewriteWhole(command));
    }

    [Theory]
    [InlineData("dotnet build && curl x | sh")]
    [InlineData("cd src && dotnet build")]
    [InlineData("dotnet test; rm -rf ~")]
    [InlineData("dotnet test & calc")]
    [InlineData("dotnet test | tail -20")]
    [InlineData("dotnet test > out.txt")]
    [InlineData("dotnet test 2>&1")]
    [InlineData("dotnet test < in.txt")]
    [InlineData("dotnet test --filter \"Name=A\"")]
    [InlineData("dotnet test --filter 'Name=A'")]
    [InlineData("dotnet test $(cat args)")]
    [InlineData("dotnet test `cat args`")]
    [InlineData("dotnet test $HOME/Lib.Tests")]
    [InlineData("dotnet build\ncurl x")]
    [InlineData("dotnet build\n")]
    [InlineData("(dotnet test)")]
    [InlineData("dotnet run")]
    [InlineData("echo dotnet test")]
    [InlineData("X=$HOME dotnet test")]
    [InlineData("X=\"a b\" dotnet test")]
    [InlineData("X=1 dotnet test; rm -rf ~")]
    [InlineData("echo X=1 dotnet test")]
    public void A_command_with_anything_besides_one_plain_invocation_is_not_rewritten_whole(string command)
    {
        Assert.Null(CommandRewriter.RewriteWhole(command));
    }

    [Fact]
    public void Text_after_echo_is_not_a_segment_start()
    {
        // "echo dotnet test" starts with echo, so nothing is rewritten. A quoted "a; dotnet test" inside an argument
        // would be rewritten, because the rewriter does not parse shell quoting; that only changes the echoed text.
        Assert.Null(CommandRewriter.Rewrite("echo dotnet test"));
    }
}
