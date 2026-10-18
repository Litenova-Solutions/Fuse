using Fuse.Check.Model;
using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class ResultsFileTests
{
    [Fact]
    public void Check_file_holds_every_returned_error_and_notes_what_the_engine_did_not_send()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        ReportedError[] errors =
        [
            new(new CompilerError("App/Program.cs", 2, 28, "CS1061", "no Add"), new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")),
            new(new CompilerError("App/Report.cs", 4, 9, "CS1503", "wrong argument")) { GeneratedFile = "gen/Report.g.cs" },
            new(new CompilerError("App/Other.cs", 9, 1, "CS1061", "no Add")) { GeneratedFile = "gen/Report.g.cs" },
        ];
        var path = ResultsFile.WriteCheck(repo.Root, new CheckReport(errors, 3, ["App"], [], 0, false, 5, 3, 0));

        Assert.Equal(Path.Combine(repo.Root.StateDirectory, "last-check-results.txt"), path);
        Assert.Equal(
            "fuse check: 5 error(s) introduced\n" +
            "App/Program.cs(2,28): error CS1061: no Add\n" +
            "  removed: public int Add(int a, int b)\n" +
            "App/Report.cs(4,9): error CS1503: wrong argument\n" +
            "  generated: gen/Report.g.cs declares it, and Fuse reads that file as the last build wrote it; if it is generated from changed code, run fuse build to write it again\n" +
            "App/Other.cs(9,1): error CS1061: no Add\n" +
            "... and 2 more error(s) the engine did not send\n",
            File.ReadAllText(path!));
    }

    [Fact]
    public void Test_file_holds_every_failure_by_project_with_its_counts()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var path = ResultsFile.WriteTests(repo.Root,
        [
            ("Lib.Tests", new TrxResults(7, 1, 0, [new TestFailure("Multiplies", "Assert.Equal() Failure\nValues differ", ["at CalcTests.Multiplies() in Lib/CalcTests.cs:line 12"])])),
            ("App.Tests", new TrxResults(3, 2, 1, [new TestFailure("Adds", "boom", []), new TestFailure("Divides", "bang\nsecond line", ["frame one"])])),
        ]);

        Assert.Equal(Path.Combine(repo.Root.StateDirectory, "last-test-results.txt"), path);
        Assert.Equal(
            "## Lib.Tests\n" +
            "FAILED Multiplies\n" +
            "  Assert.Equal() Failure\n" +
            "  Values differ\n" +
            "  at CalcTests.Multiplies() in Lib/CalcTests.cs:line 12\n" +
            "## App.Tests\n" +
            "FAILED Adds\n" +
            "  boom\n" +
            "FAILED Divides\n" +
            "  bang\n" +
            "  second line\n" +
            "  frame one\n" +
            "fuse: 3 failed, 10 passed\n",
            File.ReadAllText(path!));
    }

    [Fact]
    public void Test_file_omits_the_section_for_a_run_without_a_project()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var path = ResultsFile.WriteTests(repo.Root, [("", new TrxResults(0, 1, 0, [new TestFailure("Flakes", "boom", [])]))]);

        Assert.Equal("FAILED Flakes\n  boom\nfuse: 1 failed, 0 passed\n", File.ReadAllText(path!));
    }

    [Fact]
    public void Clean_runs_still_replace_the_file()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var testPath = ResultsFile.WriteTests(repo.Root, [("Lib.Tests", TrxResults.Empty)]);
        Assert.Equal("fuse: 0 failed, 0 passed\n", File.ReadAllText(testPath!));

        var checkPath = ResultsFile.WriteCheck(repo.Root, new CheckReport([], 1, [], [], 0, false, 0, 0, 0));
        Assert.Equal("fuse check: 0 error(s) introduced\n", File.ReadAllText(checkPath!));
    }

    [Fact]
    public void A_state_directory_that_cannot_be_written_returns_null()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        // A file where the state directory goes makes creating it fail, as an unwritable one does.
        Directory.CreateDirectory(Path.GetDirectoryName(repo.Root.StateDirectory)!);
        File.WriteAllText(repo.Root.StateDirectory, "blocked");
        try
        {
            Assert.Null(ResultsFile.WriteCheck(repo.Root, new CheckReport([], 1, [], [], 0, false, 0, 0, 0)));
            Assert.Null(ResultsFile.WriteTests(repo.Root, []));
        }
        finally
        {
            File.Delete(repo.Root.StateDirectory);
        }
    }
}
