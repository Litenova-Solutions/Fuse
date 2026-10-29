using Fuse.Operations;

namespace Fuse.Tests.Unit;

public class CheckArgumentsTests
{
    [Fact]
    public void Defaults_check_every_change_and_show_the_first_errors_as_text()
    {
        var (parsed, refusal) = CheckArguments.Parse([]);

        Assert.Null(refusal);
        Assert.NotNull(parsed);
        Assert.Empty(parsed.Files);
        Assert.Equal(0, parsed.MaxErrors);
        Assert.False(parsed.Json);
    }

    [Fact]
    public void Files_are_kept_as_named()
    {
        var (parsed, refusal) = CheckArguments.Parse(["Lib/Calc.cs", "App/Program.cs"]);

        Assert.Null(refusal);
        Assert.Equal(["Lib/Calc.cs", "App/Program.cs"], parsed!.Files);
    }

    [Fact]
    public void A_single_dash_starts_a_file()
    {
        var (parsed, refusal) = CheckArguments.Parse(["-h.cs"]);

        Assert.Null(refusal);
        Assert.Equal(["-h.cs"], parsed!.Files);
    }

    [Theory]
    [InlineData("--max-errors", "50")]
    [InlineData("--max-errors=50")]
    public void Max_errors_takes_its_count_after_a_space_or_an_equals_sign(params string[] args)
    {
        var (parsed, refusal) = CheckArguments.Parse(args);

        Assert.Null(refusal);
        Assert.Equal(50, parsed!.MaxErrors);
    }

    [Fact]
    public void All_errors_uncaps_the_errors_shown()
    {
        var (parsed, refusal) = CheckArguments.Parse(["--all-errors"]);

        Assert.Null(refusal);
        Assert.Equal(int.MaxValue, parsed!.MaxErrors);
    }

    [Fact]
    public void Json_is_read()
    {
        var (parsed, refusal) = CheckArguments.Parse(["--json", "Lib/Calc.cs"]);

        Assert.Null(refusal);
        Assert.True(parsed!.Json);
        Assert.Equal(["Lib/Calc.cs"], parsed.Files);
    }

    [Fact]
    public void After_two_dashes_every_token_is_a_file()
    {
        var (parsed, refusal) = CheckArguments.Parse(["--", "--help", "--json"]);

        Assert.Null(refusal);
        Assert.False(parsed!.Json);
        Assert.Equal(["--help", "--json"], parsed.Files);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--warnings")]
    public void An_unknown_option_is_refused(string token)
    {
        var (parsed, refusal) = CheckArguments.Parse([token]);

        Assert.Null(parsed);
        Assert.Equal($"fuse: check takes files and [--max-errors <n>|--all-errors] [--json]; '{token}' is not one", refusal);
    }

    [Fact]
    public void All_errors_and_max_errors_together_are_refused()
    {
        var (parsed, refusal) = CheckArguments.Parse(["--all-errors", "--max-errors", "5"]);

        Assert.Null(parsed);
        Assert.Equal("fuse: --all-errors and --max-errors cannot be combined; choose one", refusal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100001")]
    [InlineData("many")]
    public void A_count_outside_one_to_the_limit_is_refused(string count)
    {
        var (parsed, refusal) = CheckArguments.Parse(["--max-errors", count]);

        Assert.Null(parsed);
        Assert.StartsWith("fuse: --max-errors takes a count of ", refusal, StringComparison.Ordinal);
    }
}
