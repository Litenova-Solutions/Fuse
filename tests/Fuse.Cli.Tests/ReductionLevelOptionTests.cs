using Fuse.Cli.Commands;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli.Tests;

/// <summary>
///     Asserts the scoped commands expose a single <c>--level</c> option that defaults to the documented
///     value, replacing the former boolean reduction cluster.
/// </summary>
public sealed class ReductionLevelOptionTests
{
    [Fact]
    public void DotNetCommand_DefaultsToNone()
    {
        Assert.Equal(ReductionLevel.None, new DotNetCommand().Level);
    }

    [Fact]
    public void VerifyCommand_DefaultsToNone()
    {
        Assert.Equal(ReductionLevel.None, new VerifyCommand().Level);
    }

    [Fact]
    public void ExplainCommand_DefaultsToNone()
    {
        Assert.Equal(ReductionLevel.None, new ExplainCommand().Level);
    }

    [Theory]
    [InlineData(ReductionLevel.Standard)]
    [InlineData(ReductionLevel.Aggressive)]
    [InlineData(ReductionLevel.Skeleton)]
    [InlineData(ReductionLevel.PublicApi)]
    public void DotNetCommand_LevelIsBindable(ReductionLevel level)
    {
        var command = new DotNetCommand { Level = level };
        Assert.Equal(level, command.Level);
    }
}
