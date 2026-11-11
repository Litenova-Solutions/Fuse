using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class BuildReuseTests
{
    [Fact]
    public async Task A_recorded_build_is_read_back_whole()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var stamp = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        var errors = new List<BuildDiagnostic> { new("App/Program.cs", 3, 30, "CS1061", "no Add"), new("MSBUILD", null, null, "MSB1009", "no project") };
        var warnings = new List<BuildWarning> { new("Lib/Calc.cs", 12, 5, "CS0618", "obsolete") };
        var commands = new List<(string Command, List<string> Lines)> { ("dotnet tool", ["oops"]) };
        var finished = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        BuildResultsFile.WriteReuse(repo.Root, stamp, BuildResultsFile.WholeScope, [], ["-c", "Release"], ".", finished,
            [("build", 1, 3.5, errors, warnings, 2, commands, "slowest tasks: Csc 1.0 s", "tail line", "Fuse.slnx")],
            "; tree abc over 1 changed file(s)", null);

        var stored = BuildResultsFile.ReadReuse(repo.Root)!.Value;
        Assert.Equal((stamp.TreeHash, stamp.FullHash, stamp.AssetsHash), (stored.TreeHash, stored.FullHash, stored.AssetsHash));
        Assert.Equal(BuildResultsFile.WholeScope, stored.Scope);
        Assert.Empty(stored.Projects);
        Assert.Equal(["-c", "Release"], stored.Arguments);
        Assert.Equal(".", stored.WorkingDirectory);
        Assert.Equal(finished, stored.FinishedUtc);
        Assert.Equal("; tree abc over 1 changed file(s)", stored.TreeStamp);
        Assert.Null(stored.FileTotal);
        var run = Assert.Single(stored.Runs);
        Assert.Equal("build", run.Label);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(3.5, run.Seconds);
        Assert.Equal(errors, run.Errors);
        Assert.Equal(warnings, run.Warnings);
        Assert.Equal(2, run.WarningsCount);
        Assert.Equal(commands[0].Command, run.Commands[0].Command);
        Assert.Equal(commands[0].Lines, run.Commands[0].Lines);
        Assert.Equal("slowest tasks: Csc 1.0 s", run.Times);
        Assert.Equal("tail line", run.Tail);
        Assert.Equal("Fuse.slnx", run.RestoreTarget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"treeHash":"a","fullHash":"b","assetsHash":"c","scope":"whole","projects":[],"arguments":[],"workingDirectory":".","finishedUtc":"2026-11-06T12:00:00Z","runs":[{"label":"build"}]}""")]
    public void Missing_malformed_and_other_versions_read_as_null(string? content)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        if (content is not null)
        {
            Directory.CreateDirectory(repo.Root.StateDirectory);
            File.WriteAllText(Path.Combine(repo.Root.StateDirectory, "last-build.json"), content);
        }

        Assert.Null(BuildResultsFile.ReadReuse(repo.Root));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(14, "14 s")]
    [InlineData(89, "89 s")]
    [InlineData(90, "1 min")]
    [InlineData(300, "5 min")]
    [InlineData(5400, "1 h")]
    [InlineData(7200, "2 h")]
    [InlineData(200000, "2 d")]
    public void AgeAgo_says_seconds_minutes_hours_or_days(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, BuildResultsFile.AgeAgo(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void AgeAgo_clamps_a_time_in_the_future()
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("0 s", BuildResultsFile.AgeAgo(now.AddSeconds(5), now));
    }

    [Fact]
    public async Task The_reuse_stamp_covers_sources_project_files_and_other_files()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Calc.cs"] = "public class Calc {}\n",
            ["Lib/Lib.csproj"] = "<Project />\n",
            ["notes.md"] = "notes\n",
        });
        var clean = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;

        repo.Replace("Lib/Calc.cs", "{}", "{ }");
        var sources = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        Assert.NotEqual(clean.TreeHash, sources.TreeHash);
        Assert.NotEqual(clean.FullHash, sources.FullHash);

        repo.Replace("Lib/Calc.cs", "{ }", "{}");
        repo.Replace("Lib/Lib.csproj", "<Project />", "<Project></Project>");
        var project = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(clean.TreeHash, project.TreeHash);
        Assert.NotEqual(clean.FullHash, project.FullHash);

        repo.Replace("Lib/Lib.csproj", "<Project></Project>", "<Project />");
        repo.Replace("notes.md", "notes", "more notes");
        var other = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(clean.TreeHash, other.TreeHash);
        Assert.NotEqual(clean.FullHash, other.FullHash);
    }

    [Fact]
    public async Task A_restore_output_changes_the_assets_stamp()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var before = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;

        repo.Write("Lib/obj/project.assets.json", """{"version":1}""");
        var after = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;

        Assert.Equal(before.TreeHash, after.TreeHash);
        Assert.NotEqual(before.AssetsHash, after.AssetsHash);

        repo.Write("project.assets.json", """{"version":1}""");
        var rootLevel = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;

        Assert.Equal(after.AssetsHash, rootLevel.AssetsHash);
    }

    [Fact]
    public async Task Past_five_hundred_changed_files_there_is_no_reuse_stamp()
    {
        using var repo = FixtureRepo.CreateEmpty(Enumerable.Range(0, 501).ToDictionary(i => $"src/F{i}.cs", _ => "public class C {}\n"));
        foreach (var i in Enumerable.Range(0, 501))
            repo.Replace($"src/F{i}.cs", "{}", "{ }");

        Assert.Null(await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_identical_build_prints_the_stored_result_as_reused()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        repo.Replace("Lib/Calc.cs", "{}", "{ }");
        var stamp = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        BuildResultsFile.WriteReuse(repo.Root, stamp, BuildResultsFile.WholeScope, [], ["-c", "Release"], ".", DateTime.UtcNow.AddSeconds(-14),
            [("build", 1, 3.0, new List<BuildDiagnostic> { new("App/Program.cs", 3, 30, "CS1061", "no Add") }, new List<BuildWarning>(), 0, new List<(string Command, List<string> Lines)>(), null, "", null)],
            "; tree abc over 1 changed file(s)", null);

        var answer = BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.WholeScope, [], ["-c", "Release"], repo.Root.Path, BuildOptions.Default)!;

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        var lines = answer.Text.Split('\n');
        Assert.Matches($"^fuse: build reused from \\d+ s ago; tree {stamp.TreeHash}$", lines[0]);
        Assert.Equal("App/Program.cs(3,30): error CS1061: no Add", lines[1]);
        Assert.Equal("fuse: build failed with 1 error(s) in 3.0 s; tree abc over 1 changed file(s)", lines[2]);
        Assert.Contains("no Add", File.ReadAllText(Path.Combine(repo.Root.StateDirectory, "last-build-results.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_build_reuses_each_project_and_counts_the_failed_ones()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var stamp = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        var app = Path.Combine(repo.Root.Path, "App", "App.csproj");
        var lib = Path.Combine(repo.Root.Path, "Lib", "Lib.csproj");
        BuildResultsFile.WriteReuse(repo.Root, stamp, BuildResultsFile.ChangedScope, [app, lib], [], ".", DateTime.UtcNow.AddSeconds(-14),
            [
                ("build (App)", 1, 2.0, new List<BuildDiagnostic> { new("App/Program.cs", 1, 1, "CS1002", "; expected") }, new List<BuildWarning>(), 0, new List<(string Command, List<string> Lines)>(), null, "", "App/App.csproj"),
                ("build (Lib)", 0, 1.0, new List<BuildDiagnostic>(), new List<BuildWarning>(), 0, new List<(string Command, List<string> Lines)>(), null, "", "Lib/Lib.csproj"),
            ],
            null, 1);

        var answer = BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.ChangedScope, [app, lib], [], repo.Root.Path, BuildOptions.Default)!;

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        var lines = answer.Text.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Matches($"^fuse: build \\(App\\) reused from \\d+ s ago; tree {stamp.TreeHash}$", lines[0]);
        Assert.Equal("fuse: build (App) failed with 1 error(s) in 2.0 s", lines[2]);
        Assert.Matches($"^fuse: build \\(Lib\\) reused from \\d+ s ago; tree {stamp.TreeHash}$", lines[3]);
        Assert.Equal("fuse: build (Lib) succeeded in 1.0 s", lines[4]);
        Assert.Equal("fuse: 1 of 2 project(s) failed", lines[5]);
    }

    [Fact]
    public async Task A_build_with_other_inputs_builds_again()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        repo.Replace("Lib/Calc.cs", "{}", "{ }");
        var stamp = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        BuildResultsFile.WriteReuse(repo.Root, stamp, BuildResultsFile.WholeScope, [], ["-c", "Release"], ".", DateTime.UtcNow,
            [("build", 0, 1.0, new List<BuildDiagnostic>(), new List<BuildWarning>(), 0, new List<(string Command, List<string> Lines)>(), null, "", null)],
            null, null);

        Assert.Null(BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.WholeScope, [], ["-c", "Debug"], repo.Root.Path, BuildOptions.Default));
        Assert.Null(BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.ChangedScope, [], ["-c", "Release"], repo.Root.Path, BuildOptions.Default));
        Assert.Null(BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.WholeScope, [], ["-c", "Release"], Path.GetTempPath(), BuildOptions.Default));

        repo.Replace("Lib/Calc.cs", "{ }", "{}");
        var changed = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        Assert.Null(BuildOperation.ReusedBuild(repo.Root, changed, BuildResultsFile.WholeScope, [], ["-c", "Release"], repo.Root.Path, BuildOptions.Default));
    }

    [Fact]
    public void Without_a_recorded_build_there_is_nothing_to_reuse()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        Assert.Null(BuildOperation.ReusedBuild(repo.Root, ("a", "b", "c", 0), BuildResultsFile.WholeScope, [], [], repo.Root.Path, BuildOptions.Default));
    }

    [Fact]
    public async Task A_reused_build_lists_warnings_only_when_asked()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var stamp = (await BuildResultsFile.StampForReuseAsync(repo.Root, TestContext.Current.CancellationToken))!.Value;
        BuildResultsFile.WriteReuse(repo.Root, stamp, BuildResultsFile.WholeScope, [], [], ".", DateTime.UtcNow,
            [("build", 0, 1.0, new List<BuildDiagnostic>(), new List<BuildWarning> { new("Lib/Calc.cs", 12, 5, "CS0618", "obsolete") }, 1, new List<(string Command, List<string> Lines)>(), null, "", null)],
            null, null);

        var quiet = BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.WholeScope, [], [], repo.Root.Path, BuildOptions.Default)!;
        var loud = BuildOperation.ReusedBuild(repo.Root, stamp, BuildResultsFile.WholeScope, [], [], repo.Root.Path, new BuildOptions(20, false, true))!;

        Assert.DoesNotContain("warning CS0618", quiet.Text, StringComparison.Ordinal);
        Assert.Contains("; 1 warning(s)", quiet.Text, StringComparison.Ordinal);
        Assert.Contains("  Lib/Calc.cs(12,5): warning CS0618: obsolete", loud.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_content_hash_failure_is_not_recorded()
    {
        Assert.False(BuildOperation.ShouldRecord([new BuildDiagnostic("App/App.csproj", null, null, "NU1403", "hash validation failed")]));
        Assert.True(BuildOperation.ShouldRecord([new BuildDiagnostic("Lib/Calc.cs", 1, 1, "CS1002", "; expected")]));
        Assert.True(BuildOperation.ShouldRecord([]));
    }

    [Fact]
    public void The_restore_hint_is_the_first_project_or_solution_in_the_arguments()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        Assert.Equal("App/App.csproj", BuildOperation.RestoreTargetHint(["App/App.csproj", "-c", "Release"], repo.Root.Path, repo.Root.Path));
        Assert.Equal("Shop.slnx", BuildOperation.RestoreTargetHint(["-c", "Release", "Shop.slnx"], repo.Root.Path, repo.Root.Path));
        Assert.Null(BuildOperation.RestoreTargetHint(["-c", "Release"], repo.Root.Path, repo.Root.Path));
        Assert.Null(BuildOperation.RestoreTargetHint(["--project"], repo.Root.Path, repo.Root.Path));
    }
}
