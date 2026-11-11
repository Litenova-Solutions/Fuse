using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A change to a schema, fixture or resource selects the tests of the project that uses the file. A file no
///     project holds names itself in the plan instead of reading as no test affected.
/// </summary>
public class NonCSharpSelectionTests
{
    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    [Fact]
    public async Task An_embedded_resource_in_a_library_selects_its_dependent_test_projects_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithLibItem("Lib/data.dat", """<EmbeddedResource Include="data.dat" />"""));
        engine.Repo.Write("Lib/data.dat", "changed\n");

        var selection = await SelectAsync(engine, "Lib/data.dat");

        Assert.Equal("data.dat changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.Equal("data.dat changed", Assert.IsType<TestSelection.Whole>(selection["App.Tests"]).Reason);
    }

    [Fact]
    public async Task A_content_file_in_a_library_selects_its_dependent_test_projects_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithLibItem("Lib/content.dat", """<Content Include="content.dat" />"""));
        engine.Repo.Write("Lib/content.dat", "changed\n");

        var selection = await SelectAsync(engine, "Lib/content.dat");

        Assert.Equal("content.dat changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
    }

    [Fact]
    public async Task A_none_file_with_a_copy_to_output_in_a_library_selects_its_dependent_test_projects_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithLibItem("Lib/copy.dat", """<None Include="copy.dat" CopyToOutputDirectory="PreserveNewest" />"""));
        engine.Repo.Write("Lib/copy.dat", "changed\n");

        var selection = await SelectAsync(engine, "Lib/copy.dat");

        Assert.Equal("copy.dat changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
    }

    [Fact]
    public async Task An_additional_file_in_a_library_selects_its_dependent_test_projects_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithLibItem("Lib/extra.dat", """<AdditionalFiles Include="extra.dat" />"""));
        engine.Repo.Write("Lib/extra.dat", "changed\n");

        var selection = await SelectAsync(engine, "Lib/extra.dat");

        Assert.Equal("extra.dat changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
    }

    [Fact]
    public async Task A_non_csharp_file_in_a_test_project_selects_that_project_whole()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib.Tests/fixture.dat", "fixture\n");
        repo.Replace("Lib.Tests/Lib.Tests.csproj", "</Project>", """<ItemGroup><Content Include="fixture.dat" /></ItemGroup></Project>""");
        repo.Commit("add a fixture to Lib.Tests");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Write("Lib.Tests/fixture.dat", "changed\n");

        var selection = await SelectAsync(engine, "Lib.Tests/fixture.dat");

        Assert.Equal("fixture.dat changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.False(selection.ContainsKey("App.Tests"));
    }

    [Fact]
    public async Task A_non_csharp_file_under_a_project_directory_that_no_item_names_selects_that_projects_tests_whole()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/brand-new.json", "{}\n");

        var selection = await SelectAsync(engine, "Lib/brand-new.json");

        Assert.Equal("brand-new.json changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
    }

    [Fact]
    public async Task A_plan_for_a_non_csharp_file_outside_every_project_names_it_instead_of_saying_no_test_is_affected()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("schemas/consumer.json", "{}\n");
        await engine.SyncAsync("schemas/consumer.json");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Equal("1 non-C# file(s) outside every project changed (schemas/consumer.json); a test that reads them by path is not selected; fuse test --all covers them", plan.Summary);
        Assert.DoesNotContain("no test is affected", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_for_only_a_non_csharp_file_in_a_project_runs_its_tests_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithLibItem("Lib/data.dat", """<EmbeddedResource Include="data.dat" />"""));
        engine.Repo.Write("Lib/data.dat", "changed\n");
        await engine.SyncAsync("Lib/data.dat");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Equal(["App.Tests", "Lib.Tests"], plan.Runs.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(plan.Runs, r => Assert.IsType<RunMode.Build>(r.Mode));
        Assert.Equal(4, plan.SelectedTests);
        Assert.Contains("data.dat changed", plan.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("no C# changes", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_with_a_csharp_change_and_a_file_outside_every_project_names_the_outside_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Write("schemas/consumer.json", "{}\n");
        await engine.SyncAsync("Lib/Calc.cs", "schemas/consumer.json");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(plan.Runs);
        Assert.Contains("1 non-C# file(s) outside every project changed (schemas/consumer.json); a test that reads them by path is not selected; fuse test --all covers them", plan.Summary, StringComparison.Ordinal);
    }

    /// <summary>The standard repository with one non-C# file in Lib, named by <paramref name="item"/> in Lib's project file.</summary>
    private static FixtureRepo WithLibItem(string file, string item)
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write(file, "fixture\n");
        repo.Replace("Lib/Lib.csproj", "</Project>", $"<ItemGroup>{item}</ItemGroup></Project>");
        repo.Commit("add a non-C# file to Lib");
        return repo;
    }
}
