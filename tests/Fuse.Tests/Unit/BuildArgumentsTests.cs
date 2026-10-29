using Fuse.Operations;

namespace Fuse.Tests.Unit;

public class BuildArgumentsTests
{
    [Fact]
    public void Defaults_show_the_first_errors_as_text()
    {
        var (parsed, refusal) = BuildArguments.Parse([]);

        Assert.Null(refusal);
        Assert.NotNull(parsed);
        Assert.Empty(parsed.Passthrough);
        Assert.Equal(0, parsed.MaxErrors);
        Assert.False(parsed.Json);
        Assert.False(parsed.ListWarnings);
        Assert.False(parsed.FromRewrite);
        Assert.Equal(BuildOptions.Default, parsed.ToOptions());
    }

    [Theory]
    [InlineData("--max-errors", "5")]
    [InlineData("--max-errors=5")]
    public void Max_errors_takes_its_count_after_a_space_or_an_equals_sign(params string[] args)
    {
        var (parsed, refusal) = BuildArguments.Parse(args);

        Assert.Null(refusal);
        Assert.Equal(5, parsed!.MaxErrors);
        Assert.Equal(5, parsed.ToOptions().MaxShown);
    }

    [Fact]
    public void All_errors_uncaps_the_errors_shown()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--all-errors"]);

        Assert.Null(refusal);
        Assert.Equal(int.MaxValue, parsed!.MaxErrors);
        Assert.Equal(int.MaxValue, parsed.ToOptions().MaxShown);
    }

    [Fact]
    public void Json_uncaps_the_errors_shown()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--json"]);

        Assert.Null(refusal);
        Assert.True(parsed!.Json);
        Assert.Equal(int.MaxValue, parsed.ToOptions().MaxShown);
    }

    [Fact]
    public void Warnings_are_listed_when_asked()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--warnings"]);

        Assert.Null(refusal);
        Assert.True(parsed!.ListWarnings);
    }

    [Fact]
    public void The_rewrite_token_is_read_and_never_forwarded()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--from-rewrite", "-c", "Release"]);

        Assert.Null(refusal);
        Assert.True(parsed!.FromRewrite);
        Assert.Equal(["-c", "Release"], parsed.Passthrough);
    }

    [Fact]
    public void Dotnet_arguments_pass_through_unchanged()
    {
        var (parsed, refusal) = BuildArguments.Parse(["App/App.csproj", "-c", "Release", "--no-restore", "-p:Version=1.2.3"]);

        Assert.Null(refusal);
        Assert.Equal(["App/App.csproj", "-c", "Release", "--no-restore", "-p:Version=1.2.3"], parsed!.Passthrough);
    }

    [Fact]
    public void After_two_dashes_every_token_passes_through()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--", "--json", "--max-errors"]);

        Assert.Null(refusal);
        Assert.False(parsed!.Json);
        Assert.Equal(["--json", "--max-errors"], parsed.Passthrough);
    }

    [Theory]
    [InlineData("--max-erors", "--max-errors")]
    [InlineData("--all-error", "--all-errors")]
    [InlineData("--jsonn", "--json")]
    [InlineData("--warning", "--warnings")]
    public void A_near_miss_of_a_fuse_option_is_refused_with_what_it_meant(string token, string meant)
    {
        var (parsed, refusal) = BuildArguments.Parse([token]);

        Assert.Null(parsed);
        Assert.Equal($"fuse: '{token}' is not a fuse build option; did you mean '{meant}'?", refusal);
    }

    [Fact]
    public void An_unknown_dotnet_option_passes_through()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--dotnet-things"]);

        Assert.Null(refusal);
        Assert.Equal(["--dotnet-things"], parsed!.Passthrough);
    }

    [Fact]
    public void All_errors_and_max_errors_together_are_refused()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--all-errors", "--max-errors", "5"]);

        Assert.Null(parsed);
        Assert.Equal("fuse: --all-errors and --max-errors cannot be combined; choose one", refusal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100001")]
    [InlineData("many")]
    [InlineData("")]
    public void A_count_outside_one_to_the_limit_is_refused(string count)
    {
        var (parsed, refusal) = BuildArguments.Parse(["--max-errors", count]);

        Assert.Null(parsed);
        Assert.StartsWith("fuse: --max-errors takes a count of ", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_count_is_refused()
    {
        var (parsed, refusal) = BuildArguments.Parse(["--max-errors"]);

        Assert.Null(parsed);
        Assert.Equal("fuse: --max-errors takes a count of 1 or more; use --all-errors for every error", refusal);
    }
}
