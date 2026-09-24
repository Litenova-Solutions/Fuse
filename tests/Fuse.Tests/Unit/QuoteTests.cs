using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class QuoteTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("C:\\Program Files\\x\\", "\"C:\\Program Files\\x\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Quotes_by_command_line_rules(string argument, string expected)
    {
        Assert.Equal(expected, EngineLauncher.Quote(argument));
    }
}
