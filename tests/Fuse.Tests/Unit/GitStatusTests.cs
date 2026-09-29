using Fuse.Repo;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     The tracker's set of changed files starts from git status. The path in each record is used exactly as git wrote
///     it, because a name may begin or end with a space, hold a quote or not be ASCII, and a path spelled any other way
///     names a file that is not there.
/// </summary>
public class GitStatusTests
{
    [Fact]
    public void Each_NUL_separated_record_yields_its_path()
    {
        // The process runner ends the output with a line break, which is a record too short to hold a path.
        Assert.Equal(["Lib/Calc.cs", "Lib/New.cs", "Lib/Gone.cs"], GitStatus.Paths(" M Lib/Calc.cs\0?? Lib/New.cs\0 D Lib/Gone.cs\0\n"));
    }

    [Fact]
    public void Leading_and_trailing_spaces_stay_part_of_the_name() =>
        Assert.Equal([" lead.cs", "trail.cs ", "in side.cs"], GitStatus.Paths("??  lead.cs\0?? trail.cs \0?? in side.cs\0"));

    [Fact]
    public void Quotes_backslashes_and_non_ASCII_names_are_read_as_written() =>
        Assert.Equal(
            ["say \"hi\".cs", @"back\slash.cs", "blåbærgrød.cs", "日本語.cs"],
            GitStatus.Paths("?? say \"hi\".cs\0?? back\\slash.cs\0 M blåbærgrød.cs\0A  日本語.cs\0"));

    [Fact]
    public void A_record_too_short_to_hold_a_path_is_skipped()
    {
        Assert.Empty(GitStatus.Paths(""));
        Assert.Empty(GitStatus.Paths("\n"));
        Assert.Equal(["a.cs"], GitStatus.Paths(" M \0?? a.cs\0"));
    }

    [Fact]
    public async Task Git_reports_a_non_ASCII_name_with_a_leading_space_exactly()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        repo.Write("Lib/ blåbær.cs", "public class Blåbær {}\n");
        repo.Replace("Lib/Calc.cs", "{}", "{ }");

        var changed = await GitStatus.ChangedPathsAsync(repo.Root, TestContext.Current.CancellationToken);

        Assert.Equal([repo.Full("Lib/ blåbær.cs"), repo.Full("Lib/Calc.cs")], changed.Order(StringComparer.Ordinal));
    }
}
