using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class BuildCliRenderTests
{
    private const string Log = """
        Build started 17/10/2026 13:00:00.
        Lib/Calc.cs(12,5): warning CS0618: 'Old' is obsolete: 'use New'
        App/Program.cs(7,1): warning CS0219: The variable 'x' is assigned but its value is never used
        Build succeeded.

            2 Warning(s)
            0 Error(s)
        """;

    private static ProcessResult Failed(string output) => new(1, output);

    [Fact]
    public void Max_errors_caps_the_errors_printed_and_names_the_file()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(Failed(output), "/repo", 3, "build", new BuildOptions(5, false, false), "/state/last-build-results.txt");

        var lines = answer.Text.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Equal("fuse: build failed with 25 error(s), first 5 shown in 3.0 s; all 25 in /state/last-build-results.txt", lines[^1]);
    }

    [Fact]
    public void Every_error_prints_without_a_cap_or_a_file()
    {
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs({i},2): error CS0103: The name 'x' does not exist in the current context"));

        var answer = BuildOperation.Render(Failed(output), "/repo", 3, "build", new BuildOptions(int.MaxValue, false, false), "/state/last-build-results.txt");

        var lines = answer.Text.Split('\n');
        Assert.Equal(26, lines.Length);
        Assert.Equal("fuse: build failed with 25 error(s) in 3.0 s", lines[^1]);
    }

    [Fact]
    public void Json_prints_one_object_per_error_with_the_summary_last()
    {
        var answer = BuildOperation.Render(Failed("App/Program.cs(3,30): error CS1061: no Add"), "/repo", 3, "build", new BuildOptions(int.MaxValue, true, false));

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        var lines = answer.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("""{"file":"App/Program.cs","line":3,"column":30,"code":"CS1061","message":"no Add"}""", lines[0], StringComparison.Ordinal);
        Assert.Equal("fuse: build failed with 1 error(s) in 3.0 s", lines[^1]);
    }

    [Fact]
    public void A_clean_json_answer_is_only_the_summary()
    {
        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", new BuildOptions(int.MaxValue, true, false));

        Assert.Equal("fuse: build succeeded in 3.0 s", answer.Text);
    }

    [Fact]
    public void Warnings_are_listed_after_the_errors_and_before_the_summary()
    {
        var answer = BuildOperation.Render(Failed("Lib/Lib.cs(1,1): error CS1002: ; expected"), "/repo", 3, "build", new BuildOptions(20, false, true), null, Log);

        Assert.Equal(
            [
                "Lib/Lib.cs(1,1): error CS1002: ; expected",
                "  Lib/Calc.cs(12,5): warning CS0618: 'Old' is obsolete: 'use New'",
                "  App/Program.cs(7,1): warning CS0219: The variable 'x' is assigned but its value is never used",
                "fuse: build failed with 1 error(s) in 3.0 s; 2 warning(s)",
            ],
            answer.Text.Split('\n'));
    }

    [Fact]
    public void A_succeeded_build_lists_its_warnings_before_the_summary()
    {
        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", new BuildOptions(20, false, true), null, Log);

        Assert.Equal(
            [
                "  Lib/Calc.cs(12,5): warning CS0618: 'Old' is obsolete: 'use New'",
                "  App/Program.cs(7,1): warning CS0219: The variable 'x' is assigned but its value is never used",
                "fuse: build succeeded in 3.0 s; 2 warning(s)",
            ],
            answer.Text.Split('\n'));
    }

    [Fact]
    public void Past_twenty_warnings_the_rest_are_only_in_the_file()
    {
        var log = "Build started.\n" + string.Join('\n', Enumerable.Range(1, 25).Select(i => $"src/F{i}.cs(1,1): warning CS0168: unused {i}")) + "\nBuild succeeded.\n    25 Warning(s)\n    0 Error(s)\n";

        var answer = BuildOperation.Render(new ProcessResult(0, ""), "/repo", 3, "build", new BuildOptions(20, false, true), "/state/last-build-results.txt", log);

        var lines = answer.Text.Split('\n');
        Assert.Equal(22, lines.Length);
        Assert.Equal("  ... 5 more warning(s) in /state/last-build-results.txt", lines[^2]);
        Assert.Equal("fuse: build succeeded in 3.0 s; 25 warning(s)", lines[^1]);
    }

    [Fact]
    public void A_warning_that_is_also_an_error_prints_once_as_an_error()
    {
        const string log = "Build started.\nLib/Calc.cs(12,5): warning CS0618: 'Old' is obsolete\nBuild FAILED.\n    1 Warning(s)\n    1 Error(s)\n";

        var answer = BuildOperation.Render(Failed("Lib/Calc.cs(12,5): error CS0618: 'Old' is obsolete"), "/repo", 3, "build", new BuildOptions(20, false, true), null, log);

        Assert.Equal(
            [
                "Lib/Calc.cs(12,5): error CS0618: 'Old' is obsolete",
                "fuse: build failed with 1 error(s) in 3.0 s; 1 warning(s)",
            ],
            answer.Text.Split('\n'));
    }

    [Fact]
    public void A_locked_file_names_its_holder_under_the_error()
    {
        const string output = """App/App.csproj : error MSB3027: Could not copy "Lib/bin/Debug/Lib.dll" to "App/bin/Debug/Lib.dll". Exceeded retry count of 10. Failed. The file is locked by: "NightHall.WebApi (4212)".""";

        var answer = BuildOperation.Render(Failed(output), "/repo", 3, "build", new BuildOptions(20, false, false));

        Assert.Equal(
            [
                """App/App.csproj: error MSB3027: Could not copy "Lib/bin/Debug/Lib.dll" to "App/bin/Debug/Lib.dll". Exceeded retry count of 10. Failed. The file is locked by: "NightHall.WebApi (4212)".""",
                "  locked: App/bin/Debug/Lib.dll is held by NightHall.WebApi (pid 4212); stop that process, then run the build again",
                "fuse: build failed with 1 error(s) in 3.0 s",
            ],
            answer.Text.Split('\n'));
    }

    [Fact]
    public void The_results_file_holds_every_error_and_warning()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var errors = new List<BuildDiagnostic> { new("App/Program.cs", 3, 30, "CS1061", "no Add") };
        var warnings = new List<BuildWarning> { new("Lib/Calc.cs", 12, 5, "CS0618", "obsolete") };

        var path = BuildResultsFile.WriteBuild(repo.Root, errors, warnings, json: false);

        Assert.Equal(Path.Combine(repo.Root.StateDirectory, "last-build-results.txt"), path);
        Assert.Equal(
            "fuse build: 1 error(s)\n" +
            "App/Program.cs(3,30): error CS1061: no Add\n" +
            "## warnings\n" +
            "Lib/Calc.cs(12,5): warning CS0618: obsolete\n",
            File.ReadAllText(path!));
        Assert.False(File.Exists(Path.Combine(repo.Root.StateDirectory, "last-build-results.json")));
    }

    [Fact]
    public void A_clean_build_still_replaces_the_results_file()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        var path = BuildResultsFile.WriteBuild(repo.Root, [], [], json: true);

        Assert.Equal("fuse build: 0 error(s)\n", File.ReadAllText(path!));
        Assert.Equal("", File.ReadAllText(Path.Combine(repo.Root.StateDirectory, "last-build-results.json")));
    }
}
