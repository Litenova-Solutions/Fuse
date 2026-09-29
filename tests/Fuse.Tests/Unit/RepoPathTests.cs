using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     A path of the repository compares the way the file system does, so every spelling of one file is one key in a set
///     or a dictionary, and its relative form is the one git and the wire use. A spelling that compares wrongly is a
///     changed file the check never binds, or one it binds twice.
/// </summary>
public class RepoPathTests
{
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;

    [Fact]
    public void A_relative_an_absolute_and_a_dotted_spelling_are_one_path()
    {
        var relative = Root.PathOf("Lib/Calc.cs");
        var absolute = Root.PathOf(Path.Combine(Root.Path, "Lib", "Calc.cs"));
        var dotted = Root.PathOf("Lib/../Lib/./Calc.cs");

        Assert.Equal(Path.Combine(Root.Path, "Lib", "Calc.cs"), relative.Absolute);
        Assert.Equal(relative, absolute);
        Assert.Equal(relative, dotted);
        Assert.True(relative == absolute);
        Assert.False(relative != dotted);
        Assert.Equal(relative.GetHashCode(), absolute.GetHashCode());
        Assert.Equal(relative.GetHashCode(), dotted.GetHashCode());
    }

    [Fact]
    public void Two_casings_are_one_path_only_where_the_file_system_ignores_case()
    {
        var lower = Root.PathOf("Lib/Calc.cs");
        var upper = Root.PathOf("LIB/CALC.CS");

        Assert.Equal(OperatingSystem.IsWindows(), lower.Equals(upper));
        Assert.Equal(OperatingSystem.IsWindows(), lower == upper);
        Assert.Equal(OperatingSystem.IsWindows(), lower.Equals((object)upper));
        if (OperatingSystem.IsWindows())
            Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }

    [Fact]
    public void A_set_and_a_dictionary_find_a_path_by_another_spelling_without_a_comparer()
    {
        var set = new HashSet<RepoPath> { Root.PathOf("Lib/Calc.cs"), Root.PathOf(Path.Combine(Root.Path, "Lib", "Calc.cs")) };
        var dictionary = new Dictionary<RepoPath, string> { [Root.PathOf("Lib/Calc.cs")] = "calc" };

        Assert.Single(set);
        Assert.Contains(Root.PathOf("Lib/./Calc.cs"), set);
        Assert.Equal("calc", dictionary[Root.PathOf("App/../Lib/Calc.cs")]);
        Assert.Equal(OperatingSystem.IsWindows(), set.Contains(Root.PathOf("lib/calc.cs")));
        Assert.Equal(OperatingSystem.IsWindows(), dictionary.ContainsKey(Root.PathOf("LIB/Calc.cs")));
        Assert.DoesNotContain(Root.PathOf("Lib/Other.cs"), set);
    }

    [Fact]
    public void The_relative_form_has_forward_slashes_and_the_string_form_is_absolute()
    {
        var path = Root.PathOf(Path.Combine(Root.Path, "Lib", "Sub", "Calc.cs"));

        Assert.Equal("Lib/Sub/Calc.cs", path.Relative);
        Assert.Equal(path.Absolute, path.ToString());
        Assert.Equal("Calc.cs", path.FileName);
        Assert.Equal(".", Root.PathOf(Root.Path).Relative);
    }

    [Fact]
    public void A_path_outside_the_root_keeps_its_absolute_form_and_its_relative_form_climbs_out()
    {
        // A ProjectReference to a sibling folder names a file outside the repository; the file need not exist.
        var sibling = Path.Combine(Path.GetDirectoryName(Root.Path)!, "Sibling", "Sibling.csproj");

        var path = Root.PathOf(sibling);

        Assert.Equal(sibling, path.Absolute);
        Assert.Equal("../Sibling/Sibling.csproj", path.Relative);
        Assert.Equal(path, Root.PathOf("../Sibling/Sibling.csproj"));
        Assert.False(path.IsUnder(Root.PathOf(".")));
    }

    [Fact]
    public void A_path_through_a_link_to_the_root_is_the_path_under_the_root()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var link = repo.Root.Path + "-link";
        // A junction needs no privilege on Windows, where a symbolic link does; elsewhere a symbolic link is the link.
        if (OperatingSystem.IsWindows())
            FixtureRepo.Run(Path.GetDirectoryName(link)!, "cmd", "/c", "mklink", "/J", link, repo.Root.Path);
        else
            Directory.CreateSymbolicLink(link, repo.Root.Path);
        try
        {
            var through = repo.Root.PathOf(Path.Combine(link, "Lib", "Calc.cs"));

            Assert.Equal(repo.PathOf("Lib/Calc.cs"), through);
            Assert.Equal(repo.Full("Lib/Calc.cs"), through.Absolute);
            Assert.Equal("Lib/Calc.cs", through.Relative);
        }
        finally
        {
            // Deleting the link leaves the directory it points at.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_path_is_under_each_directory_that_holds_it_but_not_under_itself_or_a_sibling_sharing_its_prefix()
    {
        var file = Root.PathOf("Lib/Sub/Calc.cs");

        Assert.True(file.IsUnder(Root.PathOf("Lib")));
        Assert.True(file.IsUnder(Root.PathOf("Lib/Sub")));
        Assert.True(file.IsUnder(Root.PathOf(".")));
        Assert.False(file.IsUnder(file));
        Assert.False(Root.PathOf("Library/Calc.cs").IsUnder(Root.PathOf("Lib")));
        Assert.False(Root.PathOf("Lib").IsUnder(file));
        Assert.Equal(OperatingSystem.IsWindows(), file.IsUnder(Root.PathOf("LIB")));
    }

    [Fact]
    public void A_spelling_matches_only_as_written_compared_the_way_the_file_system_does()
    {
        var path = Root.PathOf("Lib/Calc.cs");

        Assert.True(path.Matches(Path.Combine(Root.Path, "Lib", "Calc.cs")));
        Assert.Equal(OperatingSystem.IsWindows(), path.Matches(Path.Combine(Root.Path, "LIB", "CALC.CS")));
        // Nothing is resolved, so a relative spelling of the same file is not the same spelling.
        Assert.False(path.Matches("Lib/Calc.cs"));
        Assert.False(path.Matches(null));
    }

    [Fact]
    public void The_root_contains_what_is_spelled_under_it_and_nothing_beside_it()
    {
        Assert.True(Root.Contains(Root.Path));
        Assert.True(Root.Contains(Path.Combine(Root.Path, "Lib", "Calc.cs")));
        Assert.False(Root.Contains(Root.Path + "-sibling"));
        Assert.False(Root.Contains(Path.GetDirectoryName(Root.Path)!));
        var upper = Path.Combine(Root.Path, "Lib").ToUpperInvariant();
        if (upper != Path.Combine(Root.Path, "Lib"))
            Assert.Equal(OperatingSystem.IsWindows(), Root.Contains(upper));
    }

    [Fact]
    public void A_path_that_holds_a_NUL_character_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => Root.PathOf("Lib/Ca\0lc.cs"));
    }
}
