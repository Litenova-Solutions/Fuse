using Fuse.Repo;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class TreeStampTests
{
    [Fact]
    public void An_empty_tree_stamps_the_empty_hash()
    {
        Assert.Equal("e3b0c44298fc", TreeStamp.Of([]));
    }

    [Fact]
    public void The_stamp_is_twelve_lowercase_hex_characters()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var stamp = TreeStamp.Of([("Lib/Calc.cs", repo.Full("Lib/Calc.cs"))]);

        Assert.Matches("^[0-9a-f]{12}$", stamp);
    }

    [Fact]
    public void File_order_does_not_change_the_stamp()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Calc.cs"] = "public class Calc {}\n",
            ["App/Program.cs"] = "System.Console.WriteLine();\n",
        });
        var calc = ("Lib/Calc.cs", repo.Full("Lib/Calc.cs"));
        var program = ("App/Program.cs", repo.Full("App/Program.cs"));

        Assert.Equal(TreeStamp.Of([calc, program]), TreeStamp.Of([program, calc]));
    }

    [Fact]
    public void Changed_content_changes_the_stamp()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var before = TreeStamp.Of([("Lib/Calc.cs", repo.Full("Lib/Calc.cs"))]);

        repo.Replace("Lib/Calc.cs", "{}", "{ }");

        Assert.NotEqual(before, TreeStamp.Of([("Lib/Calc.cs", repo.Full("Lib/Calc.cs"))]));
    }

    [Fact]
    public void A_deleted_file_stamps_without_being_read()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var stamp = TreeStamp.Of([("Lib/Gone.cs", repo.Full("Lib/Gone.cs"))]);

        Assert.Matches("^[0-9a-f]{12}$", stamp);
        Assert.Equal(stamp, TreeStamp.Of([("Lib/Gone.cs", repo.Full("Lib/Gone.cs"))]));
    }

    [Fact]
    public void An_unreadable_file_stamps_without_failing()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        // A directory cannot be read as a file, which is what a locked file looks like to the stamp.
        var stamp = TreeStamp.Of([("Lib", repo.Full("Lib"))]);

        Assert.Matches("^[0-9a-f]{12}$", stamp);
    }

    [Fact]
    public async Task The_working_tree_counts_sources_only()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Calc.cs"] = "public class Calc {}\n",
            ["notes.md"] = "notes\n",
        });
        repo.Replace("Lib/Calc.cs", "{}", "{ }");
        repo.Replace("notes.md", "notes", "more notes");
        repo.Write("Lib/New.cs", "public class New {}\n");

        var (hash, count) = await TreeStamp.ForWorkingTreeAsync(repo.Root, TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Matches("^[0-9a-f]{12}$", hash);
        Assert.Equal(hash, TreeStamp.Of([("Lib/Calc.cs", repo.Full("Lib/Calc.cs")), ("Lib/New.cs", repo.Full("Lib/New.cs"))]));
    }

    [Fact]
    public void The_summary_part_names_the_hash_and_the_count()
    {
        Assert.Equal("; tree 9f2c41aa07b3 over 87 changed file(s)", TreeStamp.SummaryPart("9f2c41aa07b3", 87));
    }
}
