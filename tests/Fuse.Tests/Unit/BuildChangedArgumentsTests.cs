using Fuse.Operations;
using Fuse.Operations.Build;
namespace Fuse.Tests.Unit;

public class BuildChangedArgumentsTests
{
    [Fact]
    public void Changed_is_off_without_the_flag()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["-c", "Release"]));

        Assert.False(parsed.Value.Changed);
    }

    [Fact]
    public void Changed_builds_the_changed_projects_and_dotnet_arguments_pass_through()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--changed", "-c", "Release"]));

        Assert.True(parsed.Value.Changed);
        Assert.Equal(["-c", "Release"], parsed.Value.Passthrough);
        Assert.Equal(BuildOptions.Default, parsed.Value.ToOptions());
    }

    [Fact]
    public void Changed_composes_with_the_other_fuse_flags()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--changed", "--json", "--warnings"]));

        Assert.True(parsed.Value.Changed);
        Assert.True(parsed.Value.Json);
        Assert.True(parsed.Value.ListWarnings);
    }

    [Fact]
    public void Changed_after_a_separator_is_a_dotnet_argument()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--", "--changed"]));

        Assert.False(parsed.Value.Changed);
        Assert.Equal(["--changed"], parsed.Value.Passthrough);
    }

    [Fact]
    public void A_near_miss_of_changed_is_refused_with_a_suggestion()
    {
        var refused = Assert.IsType<ParseResult<BuildArguments>.Refused>(BuildArguments.Parse(["--changd"]));

        Assert.Equal("fuse: '--changd' is not a fuse build option; did you mean '--changed'?", refused.Message);
    }
}
