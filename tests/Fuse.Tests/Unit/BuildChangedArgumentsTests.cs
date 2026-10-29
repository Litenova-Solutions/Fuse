using Fuse.Operations;

namespace Fuse.Tests.Unit;

public class BuildChangedArgumentsTests
{
    [Fact]
    public void Changed_is_off_without_the_flag()
    {
        var (parsed, refusal) = BuildArguments.Parse(["-c", "Release"]);

        Assert.Null(refusal);
        Assert.False(parsed!.Changed);
    }

    [Fact]
    public void Changed_builds_the_changed_projects_and_dotnet_arguments_pass_through()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--changed", "-c", "Release"]);

        Assert.Null(refusal);
        Assert.True(parsed!.Changed);
        Assert.Equal(["-c", "Release"], parsed.Passthrough);
        Assert.Equal(BuildOptions.Default, parsed.ToOptions());
    }

    [Fact]
    public void Changed_composes_with_the_other_fuse_flags()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--changed", "--json", "--warnings"]);

        Assert.Null(refusal);
        Assert.True(parsed!.Changed);
        Assert.True(parsed.Json);
        Assert.True(parsed.ListWarnings);
    }

    [Fact]
    public void Changed_after_a_separator_is_a_dotnet_argument()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--", "--changed"]);

        Assert.Null(refusal);
        Assert.False(parsed!.Changed);
        Assert.Equal(["--changed"], parsed.Passthrough);
    }

    [Fact]
    public void A_near_miss_of_changed_is_refused_with_a_suggestion()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--changd"]);

        Assert.Null(parsed);
        Assert.Equal("fuse: '--changd' is not a fuse build option; did you mean '--changed'?", refusal);
    }
}
