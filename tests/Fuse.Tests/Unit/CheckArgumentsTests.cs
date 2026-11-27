using Fuse.Identity;
using Fuse.Operations;
using Fuse.Operations.Check;
namespace Fuse.Tests.Unit;

public class CheckArgumentsTests
{
    private static CheckArguments Parsed(ParseResult<CheckArguments> parsed) =>
        Assert.IsType<ParseResult<CheckArguments>.Parsed>(parsed).Value;

    private static string Refused(ParseResult<CheckArguments> parsed) =>
        Assert.IsType<ParseResult<CheckArguments>.Refused>(parsed).Message;

    [Fact]
    public void Defaults_check_every_change_and_show_the_first_errors_as_text()
    {
        var arguments = Parsed(CheckArguments.Parse([]));

        Assert.Empty(arguments.Files);
        Assert.Equal(ErrorLimit.Default, arguments.Limit);
        Assert.False(arguments.Json);
    }

    [Fact]
    public void Files_are_kept_as_named()
    {
        var arguments = Parsed(CheckArguments.Parse(["Lib/Calc.cs", "App/Program.cs"]));

        Assert.Equal(["Lib/Calc.cs", "App/Program.cs"], arguments.Files);
    }

    [Fact]
    public void A_single_dash_starts_a_file()
    {
        var arguments = Parsed(CheckArguments.Parse(["-h.cs"]));

        Assert.Equal(["-h.cs"], arguments.Files);
    }

    [Theory]
    [InlineData("--max-errors", "50")]
    [InlineData("--max-errors=50")]
    public void Max_errors_takes_its_count_after_a_space_or_an_equals_sign(params string[] args)
    {
        var arguments = Parsed(CheckArguments.Parse(args));

        Assert.Equal(new ErrorLimit.UpTo(50), arguments.Limit);
    }

    [Fact]
    public void All_errors_uncaps_the_errors_shown()
    {
        var arguments = Parsed(CheckArguments.Parse(["--all-errors"]));

        Assert.Equal(ErrorLimit.All, arguments.Limit);
    }

    [Fact]
    public void Json_is_read()
    {
        var arguments = Parsed(CheckArguments.Parse(["--json", "Lib/Calc.cs"]));

        Assert.True(arguments.Json);
        Assert.Equal(["Lib/Calc.cs"], arguments.Files);
    }

    [Fact]
    public void After_two_dashes_every_token_is_a_file()
    {
        var arguments = Parsed(CheckArguments.Parse(["--", "--help", "--json"]));

        Assert.False(arguments.Json);
        Assert.Equal(["--help", "--json"], arguments.Files);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--warnings")]
    public void An_unknown_option_is_refused(string token)
    {
        var message = Refused(CheckArguments.Parse([token]));

        Assert.Equal($"fuse: check takes files and [--max-errors <n>|--all-errors] [--json]; '{token}' is not one", message);
    }

    [Fact]
    public void All_errors_and_max_errors_together_are_refused()
    {
        var message = Refused(CheckArguments.Parse(["--all-errors", "--max-errors", "5"]));

        Assert.Equal("fuse: --all-errors and --max-errors cannot be combined; choose one", message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100001")]
    [InlineData("many")]
    public void A_count_outside_one_to_the_limit_is_refused(string count)
    {
        var message = Refused(CheckArguments.Parse(["--max-errors", count]));

        Assert.StartsWith("fuse: --max-errors takes a count of ", message, StringComparison.Ordinal);
    }
}
