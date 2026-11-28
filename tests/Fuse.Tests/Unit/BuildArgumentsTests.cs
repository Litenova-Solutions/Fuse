using Fuse.Operations;
using Fuse.Operations.Build;
namespace Fuse.Tests.Unit;

public class BuildArgumentsTests
{
    [Fact]
    public void Defaults_show_the_first_errors_as_text()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse([]));

        Assert.Empty(parsed.Value.Passthrough);
        Assert.Equal(0, parsed.Value.MaxErrors.ToCount());
        Assert.False(parsed.Value.Json);
        Assert.False(parsed.Value.ListWarnings);
        Assert.False(parsed.Value.FromRewrite);
        Assert.Equal(BuildOptions.Default, parsed.Value.ToOptions());
    }

    [Theory]
    [InlineData("--max-errors", "5")]
    [InlineData("--max-errors=5")]
    public void Max_errors_takes_its_count_after_a_space_or_an_equals_sign(params string[] args)
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(args));

        Assert.Equal(5, parsed.Value.MaxErrors.ToCount());
        Assert.Equal(5, parsed.Value.ToOptions().MaxShown);
    }

    [Fact]
    public void All_errors_uncaps_the_errors_shown()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--all-errors"]));

        Assert.Equal(int.MaxValue, parsed.Value.MaxErrors.ToCount());
        Assert.Equal(int.MaxValue, parsed.Value.ToOptions().MaxShown);
    }

    [Fact]
    public void Json_uncaps_the_errors_shown()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--json"]));

        Assert.True(parsed.Value.Json);
        Assert.Equal(int.MaxValue, parsed.Value.ToOptions().MaxShown);
    }

    [Fact]
    public void Warnings_are_listed_when_asked()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--warnings"]));

        Assert.True(parsed.Value.ListWarnings);
    }

    [Fact]
    public void The_rewrite_token_is_read_and_never_forwarded()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--from-rewrite", "-c", "Release"]));

        Assert.True(parsed.Value.FromRewrite);
        Assert.Equal(["-c", "Release"], parsed.Value.Passthrough);
    }

    [Fact]
    public void Dotnet_arguments_pass_through_unchanged()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["App/App.csproj", "-c", "Release", "--no-restore", "-p:Version=1.2.3"]));

        Assert.Equal(["App/App.csproj", "-c", "Release", "--no-restore", "-p:Version=1.2.3"], parsed.Value.Passthrough);
    }

    [Fact]
    public void After_two_dashes_every_token_passes_through()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--", "--json", "--max-errors"]));

        Assert.False(parsed.Value.Json);
        Assert.Equal(["--json", "--max-errors"], parsed.Value.Passthrough);
    }

    [Theory]
    [InlineData("--max-erors", "--max-errors")]
    [InlineData("--all-error", "--all-errors")]
    [InlineData("--jsonn", "--json")]
    [InlineData("--warning", "--warnings")]
    public void A_near_miss_of_a_fuse_option_is_refused_with_what_it_meant(string token, string meant)
    {
        var refused = Assert.IsType<ParseResult<BuildArguments>.Refused>(BuildArguments.Parse([token]));

        Assert.Equal($"fuse: '{token}' is not a fuse build option; did you mean '{meant}'?", refused.Message);
    }

    [Fact]
    public void An_unknown_dotnet_option_passes_through()
    {
        var parsed = Assert.IsType<ParseResult<BuildArguments>.Parsed>(BuildArguments.Parse(["--dotnet-things"]));

        Assert.Equal(["--dotnet-things"], parsed.Value.Passthrough);
    }

    [Fact]
    public void All_errors_and_max_errors_together_are_refused()
    {
        var refused = Assert.IsType<ParseResult<BuildArguments>.Refused>(BuildArguments.Parse(["--all-errors", "--max-errors", "5"]));

        Assert.Equal("fuse: --all-errors and --max-errors cannot be combined; choose one", refused.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100001")]
    [InlineData("many")]
    [InlineData("")]
    public void A_count_outside_one_to_the_limit_is_refused(string count)
    {
        var refused = Assert.IsType<ParseResult<BuildArguments>.Refused>(BuildArguments.Parse(["--max-errors", count]));

        Assert.StartsWith("fuse: --max-errors takes a count of ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_count_is_refused()
    {
        var refused = Assert.IsType<ParseResult<BuildArguments>.Refused>(BuildArguments.Parse(["--max-errors"]));

        Assert.Equal("fuse: --max-errors takes a count of 1 or more; use --all-errors for every error", refused.Message);
    }
}
