using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>
///     A session file for <c>fuse session begin --from</c>: each line is a name, a tab and a pattern, a name on
///     several lines holds every pattern its lines name, and a line that does not read that way refuses the file
///     before anything is registered.
/// </summary>
public class SessionFileTests
{
    [Fact]
    public void Lines_name_each_session_with_its_patterns_in_the_order_first_named()
    {
        var (registrations, refusal) = SessionOperation.ParseSessionFileLines(["w2\tApp/**", "w1\tLib/**", "", "w1\tApp/*.cs\tLib/*.cs"]);

        Assert.Null(refusal);
        Assert.Equal(["w2", "w1"], registrations.Select(r => r.Name));
        Assert.Equal(["App/**"], registrations[0].Patterns);
        Assert.Equal(["Lib/**", "App/*.cs", "Lib/*.cs"], registrations[1].Patterns);
    }

    [Fact]
    public void A_line_with_no_tab_refuses_the_file_and_names_the_line()
    {
        var (registrations, refusal) = SessionOperation.ParseSessionFileLines(["w1\tLib/**", "w2 App/**"]);

        Assert.Empty(registrations);
        Assert.Equal("fuse: session file line 2 is not a name, a tab and a pattern; write each line as a name, a tab, then a pattern", refusal);
    }

    [Theory]
    [InlineData("\tLib/**")]
    [InlineData("w1\t")]
    [InlineData("w1\t\tLib/**")]
    public void A_line_with_an_empty_name_or_pattern_refuses_the_file(string line)
    {
        var (registrations, refusal) = SessionOperation.ParseSessionFileLines([line]);

        Assert.Empty(registrations);
        Assert.Equal("fuse: session file line 1 is not a name, a tab and a pattern; write each line as a name, a tab, then a pattern", refusal);
    }

    [Fact]
    public void A_file_that_names_no_session_refuses()
    {
        var (registrations, refusal) = SessionOperation.ParseSessionFileLines(["", "  "]);

        Assert.Empty(registrations);
        Assert.Equal("fuse: the session file names no session; write each line as a name, a tab, then a pattern", refusal);
    }
}
