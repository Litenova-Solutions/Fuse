using Fuse.Dotnet;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class TestSelectionTests
{
    private static Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files) =>
        SelectAsync(engine, engine.Selector, files);

    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, TestSelector selector, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var selection = await selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return selection.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    [Fact]
    public async Task Changed_method_selects_only_the_tests_that_reach_it()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selection = await SelectAsync(engine, "Lib/Calc.cs");
        var lib = Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]);
        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], lib.Patterns);
        Assert.False(selection.TryGetValue("App.Tests", out var app) && app is not TestSelection.Methods { Patterns.Count: 0 });
    }

    [Fact]
    public async Task Implementation_reached_through_its_interface_selects_the_interface_callers()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "\"Hello \" + name", "\"Hello, \" + name");
        var selection = await SelectAsync(engine, "Lib/Greeting.cs");
        var lib = Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]);
        Assert.Contains("Lib.Tests.GreeterTests.Greets", lib.Patterns);
        Assert.DoesNotContain(lib.Patterns, p => p.Contains("CalcTests", StringComparison.Ordinal));
        // The app's top-level statements call Greet, and the runtime calls the entry point, so App.Tests runs whole.
        var app = Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
        Assert.Equal("App's entry point calls the changed code", app.Reason);
    }

    [Fact]
    public async Task Helper_in_a_test_class_selects_the_whole_class()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib.Tests/CalcTests.cs", "private static Calc NewCalc() => new();", "private static Calc NewCalc() => new Calc();");
        var selection = await SelectAsync(engine, "Lib.Tests/CalcTests.cs");
        Assert.Equal(["Lib.Tests.CalcTests."], Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns);
    }

    [Fact]
    public async Task Changed_helper_class_in_a_test_project_selects_the_tests_that_call_it()
    {
        await using var engine = await InProcessEngine.StartAsync(WithNumbersHelper());
        engine.Repo.Replace("Lib.Tests/Numbers.cs", "public static int Two() => 2;", "public static int Two() => 3;");
        var selection = await SelectAsync(engine, "Lib.Tests/Numbers.cs");
        Assert.Contains("Lib.Tests.CalcTests.Doubles", Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns);
    }

    [Fact]
    public async Task Change_reached_through_a_helper_class_in_a_test_project_selects_the_tests_that_call_the_helper()
    {
        await using var engine = await InProcessEngine.StartAsync(WithNumbersHelper());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selection = await SelectAsync(engine, "Lib/Calc.cs");
        var lib = Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns;
        Assert.Contains("Lib.Tests.CalcTests.Multiplies", lib);
        Assert.Contains("Lib.Tests.CalcTests.MultipliesThroughHelper", lib);
    }

    /// <summary>
    ///     The standard repository with a committed helper class in Lib.Tests that holds no tests, and tests in CalcTests
    ///     that reach Lib only through it.
    /// </summary>
    private static FixtureRepo WithNumbersHelper()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib.Tests/Numbers.cs", """
            using Lib;

            namespace Lib.Tests;

            internal static class Numbers
            {
                public static int Two() => 2;

                public static int Product(int a, int b) => new Calc().Mul(a, b);
            }
            """);
        repo.Replace("Lib.Tests/CalcTests.cs", "    private static Calc NewCalc() => new();", """
                [Fact]
                public void Doubles() => Assert.Equal(4, NewCalc().Add(Numbers.Two(), 2));

                [Fact]
                public void MultipliesThroughHelper() => Assert.Equal(6, Numbers.Product(2, 3));

                private static Calc NewCalc() => new();
            """);
        repo.Commit("add a helper class to Lib.Tests");
        return repo;
    }

    [Fact]
    public async Task Changed_test_method_selects_itself()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib.Tests/CalcTests.cs", "Assert.Equal(6, NewCalc().Mul(2, 3))", "Assert.Equal(8, NewCalc().Mul(2, 4))");
        var selection = await SelectAsync(engine, "Lib.Tests/CalcTests.cs");
        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns);
    }

    [Fact]
    public async Task Top_level_statements_select_dependent_test_projects_whole()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(2, 2)");
        var selection = await SelectAsync(engine, "App/Program.cs");
        var app = Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
        // The seeds select App.Tests whole for the top-level statements, and the type walk reaches the application too;
        // the member-level answer gives the reason the class-level answer gives.
        Assert.Equal("App uses the changed code and runs behind an application host", app.Reason);
        Assert.False(selection.ContainsKey("Lib.Tests"));
    }

    [Fact]
    public async Task Change_reaching_no_test_selects_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Multi/Shape.cs", "=> count;", "=> count + 0;");
        var selection = await SelectAsync(engine, "Multi/Shape.cs");
        Assert.Empty(selection);
    }

    [Fact]
    public async Task A_member_walk_over_its_budget_selects_at_class_level()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        // Every reading of this clock is 9 seconds after the last, so the walk is past its 8 second budget at its first check.
        var selection = await SelectAsync(engine, new TestSelector(engine.Workspace, new LeapingClock()), "Lib/Calc.cs");

        // The type walk selects the whole class, not only Multiplies, and reaches the application through Program.cs.
        Assert.Equal(["Lib.Tests.CalcTests."], Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns);
        Assert.Equal("App uses the changed code and runs behind an application host", Assert.IsType<TestSelection.Whole>(selection["App.Tests"]).Reason);
    }

    [Fact]
    public async Task Dependent_test_projects_include_a_test_project_itself()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var graph = engine.Workspace.Graph;
        string[] Names(string project) =>
            [.. HostRule.DependentTestProjects(graph, graph.Projects.Single(p => p.Name == project)).Select(p => p.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["App.Tests", "Lib.Tests"], Names("Lib"));
        Assert.Equal(["App.Tests"], Names("App"));
        Assert.Equal(["Lib.Tests"], Names("Lib.Tests"));
        Assert.Empty(Names("Multi"));
    }

    [Fact]
    public async Task Plan_uses_a_shadow_run_when_build_output_is_current()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var run = Assert.Single(plan.Runs);
        Assert.Equal("Lib.Tests", run.Name);
        var shadow = Assert.IsType<RunMode.Shadow>(run.Mode);
        Assert.True(File.Exists(shadow.Assembly));
        Assert.Equal("FullyQualifiedName~Lib.Tests.CalcTests.Multiplies", run.Filter);
        Assert.False(run.UsesTestingPlatform);
        Assert.Equal(1, plan.SelectedTests);
        Assert.Equal(4, plan.TotalTests);
        Assert.Equal("ran 1 test(s) affected by your changes out of 4; fuse test --all runs everything", plan.Summary);
    }

    [Fact]
    public async Task Plan_carries_the_runsettings_file_a_project_names()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib.Tests/Lib.Tests.csproj", "<IsPackable>false</IsPackable>", "<IsPackable>false</IsPackable><RunSettingsFilePath>probe.runsettings</RunSettingsFilePath>");
        repo.Commit();
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        // A relative path is read from the project's folder.
        Assert.Equal(engine.Repo.Full("Lib.Tests/probe.runsettings"), Assert.Single(plan.Runs).RunSettings);
    }

    [Fact]
    public async Task A_testing_platform_project_runs_whole_and_the_summary_counts_all_its_tests()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        repo.Replace("Lib.Tests/Lib.Tests.csproj", "<IsPackable>false</IsPackable>", "<IsPackable>false</IsPackable><IsTestingPlatformApplication>true</IsTestingPlatformApplication>");
        repo.Commit();
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        // The file watcher reports the edit a moment later, later still on macOS.
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        // One test reaches the change, but the project runs whole, so the summary counts every test in it.
        var run = Assert.Single(plan.Runs);
        Assert.True(run.UsesTestingPlatform);
        Assert.Null(run.Filter);
        // Lib.Tests has three tests: Adds and Multiplies in CalcTests, and Greets in GreeterTests.
        Assert.Equal(3, plan.SelectedTests);
        Assert.StartsWith("ran 3 test(s) affected by your changes out of 4", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_falls_back_to_msbuild_when_a_non_source_file_changed()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Write("Lib/data.json", "{}");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.IsType<RunMode.Build>(Assert.Single(plan.Runs).Mode);
    }

    [Fact]
    public async Task Plan_emits_a_dependency_that_was_built_on_its_own_after_the_test_project()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        // Lib's own build output is now newer than its sources, and Lib.Tests still holds the copy of Lib from its last build.
        Build(engine.Repo, "Lib/Lib.csproj");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var run = Assert.Single(plan.Runs);
        var shadow = Assert.IsType<RunMode.Shadow>(run.Mode);

        // The shadow run loads Lib as the working tree has it, so Multiplies fails there as it does under dotnet test.
        var outcome = await RunAsync(engine.Repo, shadow, run.Filter);
        Assert.Equal((1, 0), (outcome.Failed, outcome.Passed));
    }

    [Fact]
    public async Task Plan_after_a_build_of_the_test_project_emits_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        // Building the test project builds Lib and copies it, so every assembly the test loads is current.
        Build(engine.Repo, "Lib.Tests/Lib.Tests.csproj");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var run = Assert.Single(plan.Runs);
        var shadow = Assert.IsType<RunMode.Shadow>(run.Mode);

        // A copied assembly keeps the write time of the build output it came from; an emitted one has the time it was written.
        var directory = Path.GetDirectoryName(shadow.Assembly)!;
        foreach (var built in new[] { "Lib/bin/Debug/net10.0/Lib.dll", "Lib.Tests/bin/Debug/net10.0/Lib.Tests.dll" })
            Assert.Equal(File.GetLastWriteTimeUtc(engine.Repo.Full(built)), File.GetLastWriteTimeUtc(Path.Combine(directory, Path.GetFileName(built))));
        var outcome = await RunAsync(engine.Repo, shadow, run.Filter);
        Assert.Equal((1, 0), (outcome.Failed, outcome.Passed));
    }

    [Fact]
    public async Task Plan_with_no_changes_runs_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Empty(plan.Runs);
        Assert.Contains("no C# changes", plan.Summary, StringComparison.Ordinal);
        var all = await engine.Planner.PlanAsync(new TestScope.All(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Runs.Count);
        Assert.All(all.Runs, r => Assert.Null(r.Filter));
        Assert.All(all.Runs, r => Assert.IsType<RunMode.Build>(r.Mode));
    }

    [Fact]
    public async Task Plan_for_a_change_no_test_is_affected_by_runs_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Multi/Shape.cs", "=> count;", "=> count + 0;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Empty(plan.Runs);
        Assert.Equal(0, plan.SelectedTests);
        Assert.Equal("no test is affected by the changes (out of 4); fuse test --all runs everything", plan.Summary);
    }

    /// <summary>Builds one project of the fixture with MSBuild, as an agent or a person would, without restoring.</summary>
    private static void Build(FixtureRepo repo, string project) =>
        FixtureRepo.Run(repo.Root.Path, "dotnet", "build", project, "--no-restore", "-nologo", "-v:q");

    /// <summary>Runs a shadow run's test assembly with <c>dotnet test</c>, the way <c>fuse test</c> does, and reads its results.</summary>
    private static async Task<TrxResults> RunAsync(FixtureRepo repo, RunMode.Shadow shadow, string? filter)
    {
        var results = FixtureRepo.NewDirectory();
        try
        {
            string[] arguments = ["test", shadow.Assembly, .. filter is null ? Array.Empty<string>() : ["--filter", filter], "--logger", "trx", "--results-directory", results];
            var run = await ProcessRunner.RunAsync("dotnet", arguments, repo.Root.Path, TestContext.Current.CancellationToken);
            return TrxReader.ReadDirectory(results, repo.Root.Path) ?? throw new InvalidOperationException($"dotnet test wrote no results (exit code {run.ExitCode}):\n{run.Output}");
        }
        finally
        {
            Directory.Delete(results, recursive: true);
        }
    }

    /// <summary>A clock whose every reading is 9 seconds after the previous one.</summary>
    private sealed class LeapingClock : TimeProvider
    {
        private long _now;

        public override long GetTimestamp() => _now += TimestampFrequency * 9;
    }
}
