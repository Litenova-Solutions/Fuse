using Fuse.Testing;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class TestSelectionTests
{
    private static async Task<Dictionary<string, ProjectSelection>> SelectAsync(EngineHarness engine, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.Full), TestContext.Current.CancellationToken);
        var selection = await engine.Selector.SelectAsync(files.Select(engine.Repo.Full).ToList(), TestContext.Current.CancellationToken);
        return selection.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key), kv => kv.Value);
    }

    [Fact]
    public async Task Changed_method_selects_only_the_tests_that_reach_it()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selection = await SelectAsync(engine, "Lib/Calc.cs");
        var lib = selection["Lib.Tests"];
        Assert.False(lib.All);
        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], lib.Patterns);
        Assert.False(selection.TryGetValue("App.Tests", out var app) && (app.All || app.Patterns.Count > 0));
    }

    [Fact]
    public async Task Implementation_reached_through_its_interface_selects_the_interface_callers()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "\"Hello \" + name", "\"Hello, \" + name");
        var selection = await SelectAsync(engine, "Lib/Greeting.cs");
        Assert.Contains("Lib.Tests.GreeterTests.Greets", selection["Lib.Tests"].Patterns);
        Assert.DoesNotContain(selection["Lib.Tests"].Patterns, p => p.Contains("CalcTests", StringComparison.Ordinal));
        // The app's top-level statements call Greet, and the host is invoked by the runtime, so App.Tests runs whole.
        Assert.True(selection["App.Tests"].All);
    }

    [Fact]
    public async Task Helper_in_a_test_class_selects_the_whole_class()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib.Tests/CalcTests.cs", "private static Calc NewCalc() => new();", "private static Calc NewCalc() => new Calc();");
        var selection = await SelectAsync(engine, "Lib.Tests/CalcTests.cs");
        Assert.Equal(["Lib.Tests.CalcTests."], selection["Lib.Tests"].Patterns);
    }

    [Fact]
    public async Task Changed_test_method_selects_itself()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib.Tests/CalcTests.cs", "Assert.Equal(6, NewCalc().Mul(2, 3))", "Assert.Equal(8, NewCalc().Mul(2, 4))");
        var selection = await SelectAsync(engine, "Lib.Tests/CalcTests.cs");
        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], selection["Lib.Tests"].Patterns);
    }

    [Fact]
    public async Task Top_level_statements_select_dependent_test_projects_whole()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(2, 2)");
        var selection = await SelectAsync(engine, "App/Program.cs");
        var app = selection["App.Tests"];
        Assert.True(app.All);
        Assert.NotNull(app.AllReason);
        Assert.False(selection.ContainsKey("Lib.Tests"));
    }

    [Fact]
    public async Task Change_reaching_no_test_selects_nothing()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Multi/Shape.cs", "=> count;", "=> count + 0;");
        var selection = await SelectAsync(engine, "Multi/Shape.cs");
        Assert.Empty(selection);
    }

    [Fact]
    public async Task Plan_uses_the_fast_path_when_build_output_is_current()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(all: false, TestContext.Current.CancellationToken);
        var run = Assert.Single(plan.Runs);
        Assert.Equal("Lib.Tests", run.Name);
        Assert.NotNull(run.ShadowAssembly);
        Assert.True(File.Exists(run.ShadowAssembly));
        Assert.Equal("FullyQualifiedName~Lib.Tests.CalcTests.Multiplies", run.Filter);
        Assert.Equal(1, plan.SelectedTests);
        Assert.Equal(4, plan.TotalTests);
    }

    [Fact]
    public async Task Plan_falls_back_to_msbuild_when_a_non_source_file_changed()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Write("Lib/data.json", "{}");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(all: false, TestContext.Current.CancellationToken);
        Assert.Null(Assert.Single(plan.Runs).ShadowAssembly);
    }

    [Fact]
    public async Task Plan_with_no_changes_runs_nothing()
    {
        await using var engine = await EngineHarness.StartAsync();
        var plan = await engine.Planner.PlanAsync(all: false, TestContext.Current.CancellationToken);
        Assert.Empty(plan.Runs);
        Assert.Contains("no C# changes", plan.Scope, StringComparison.Ordinal);
        var all = await engine.Planner.PlanAsync(all: true, TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Runs.Length);
        Assert.All(all.Runs, r => Assert.Null(r.Filter));
    }
}
