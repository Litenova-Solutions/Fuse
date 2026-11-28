using Fuse.Engine.Routing;
using Fuse.Inspection;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

public class ReportBuilderTests
{
    [Fact]
    public async Task Graph_lists_projects_references_and_test_owners_in_path_order()
    {
        await using var engine = await InProcessEngine.StartAsync();

        var report = FeatureReports.ToGraphReport(GraphAnalysis.Build(engine.Workspace.PinGeneration()));

        Assert.Equal(
            ["App.Tests/App.Tests.csproj", "App/App.csproj", "Lib.Tests/Lib.Tests.csproj", "Lib/Lib.csproj", "Multi/Multi.csproj", "Strict/Strict.csproj"],
            report.Projects.Select(project => project.Path));
        var app = Assert.Single(report.Projects, project => project.Name == "App");
        Assert.Equal(["Lib"], app.References);
        Assert.Equal(["App.Tests"], app.ReferencedBy);
        Assert.False(app.IsTestProject);
        Assert.Equal(["App/Program.cs", "App/Report.cs"], app.Files);
        var lib = Assert.Single(report.Projects, project => project.Name == "Lib");
        Assert.Empty(lib.References);
        Assert.Equal(["App", "Lib.Tests"], lib.ReferencedBy);
        Assert.Equal(["Lib/Calc.cs", "Lib/Formatter.cs", "Lib/Greeting.cs"], lib.Files);
        Assert.Equal(["App", "Lib"], Assert.Single(report.TestOwners, owner => owner.TestProject == "App.Tests").CoveredProjects);
        Assert.Equal(["Lib"], Assert.Single(report.TestOwners, owner => owner.TestProject == "Lib.Tests").CoveredProjects);
        var cluster = Assert.Single(report.Clusters);
        Assert.Equal("App+Lib", cluster.Name);
        Assert.Equal(["App", "Lib"], cluster.Projects);
        Assert.Empty(cluster.SharedDependencies);
    }

    [Fact]
    public async Task Impact_of_a_lib_change_reaches_app_and_selects_its_tests()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var path = engine.Repo.PathOf("Lib/Calc.cs");
        var generation = await engine.Workspace.SyncAndPinAsync([path], TestContext.Current.CancellationToken);

        var report = FeatureReports.ToImpactReport(await ChangeImpact.BuildAsync(engine.Workspace, generation, ["Lib/Calc.cs"], TestContext.Current.CancellationToken));

        Assert.Equal(["Lib/Calc.cs"], report.InputPaths);
        Assert.Equal(["Lib"], report.OwningProjects);
        Assert.Equal(["App.Tests", "App", "Lib.Tests", "Lib"], report.AffectedProjects);
        Assert.Contains("App/Program.cs", report.AffectedFiles);
        Assert.Contains("Lib.Tests/CalcTests.cs", report.AffectedFiles);
        Assert.Contains("Lib.Tests", report.AffectedTestProjects);
        Assert.False(report.BroadReach);
        Assert.Equal($"4 affected project(s), {report.AffectedTestProjects.Length} affected test project(s)", report.Summary);

        var changed = FeatureReports.ToImpactReport(await ChangeImpact.BuildAsync(engine.Workspace, engine.Workspace.PinGeneration(), [], TestContext.Current.CancellationToken));
        Assert.Equal(report.InputPaths, changed.InputPaths);
        Assert.Equal(report.OwningProjects, changed.OwningProjects);
        Assert.Equal(report.AffectedProjects, changed.AffectedProjects);
    }

    [Fact]
    public async Task Impact_of_a_solution_is_broad_and_names_every_test_project()
    {
        await using var engine = await InProcessEngine.StartAsync();

        var report = FeatureReports.ToImpactReport(await ChangeImpact.BuildAsync(engine.Workspace, engine.Workspace.PinGeneration(), ["Fixture.slnx"], TestContext.Current.CancellationToken));

        Assert.True(report.BroadReach);
        Assert.Empty(report.OwningProjects);
        Assert.Equal(["App.Tests", "App", "Lib.Tests", "Lib", "Multi", "Strict"], report.AffectedProjects);
        Assert.Equal(["App.Tests", "Lib.Tests"], report.AffectedTestProjects);
        Assert.Contains("Lib/Calc.cs", report.AffectedFiles);
        Assert.Empty(engine.Workspace.Current.Projects);
    }
}
