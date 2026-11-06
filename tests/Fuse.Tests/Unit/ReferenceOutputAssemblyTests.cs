using Fuse.Graph;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     A reference with ReferenceOutputAssembly false builds the referenced project first and references no
///     assembly. A test project that starts an application as a child process declares the application that way, so
///     the reference is its reach to the application.
/// </summary>
public class ReferenceOutputAssemblyTests
{
    [Fact]
    public async Task Evaluation_reads_a_reference_with_reference_output_assembly_false_as_build_only()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Lib.csproj"] = LibraryProject(),
            ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
            ["App/App.csproj"] = AppProject(),
            ["App/Program.cs"] = "System.Console.WriteLine(1);\n",
            ["Lib.Tests/Lib.Tests.csproj"] = TestProject("""<ProjectReference Include="../Lib/Lib.csproj" />"""),
            ["Lib.Tests/Tests.cs"] = "public class Tests {}\n",
            ["App.Tests/App.Tests.csproj"] = TestProject("""<ProjectReference Include="../App/App.csproj" ReferenceOutputAssembly="false" />"""),
            ["App.Tests/Tests.cs"] = "public class Tests {}\n",
        });

        var graph = await RepoGraph.EvaluateAsync(repo.Root, TestContext.Current.CancellationToken);
        var app = graph.Find(repo.PathOf("App/App.csproj"))!;
        var appTests = graph.Find(repo.PathOf("App.Tests/App.Tests.csproj"))!;
        var lib = graph.Find(repo.PathOf("Lib/Lib.csproj"))!;
        var libTests = graph.Find(repo.PathOf("Lib.Tests/Lib.Tests.csproj"))!;

        Assert.Equal([repo.PathOf("App/App.csproj")], appTests.BuildOnlyReferences);
        Assert.Empty(libTests.BuildOnlyReferences);
        Assert.Contains(appTests, HostRule.DependentTestProjects(graph, app));
        Assert.Equal([appTests], HostRule.BuildOnlyDependentTestProjects(graph, app));
        Assert.Equal([appTests], HostRule.BuildOnlyDependentTestProjects(graph, lib));
        Assert.DoesNotContain(libTests, HostRule.BuildOnlyDependentTestProjects(graph, lib));
    }

    [Fact]
    public async Task A_change_in_an_application_selects_a_test_project_that_references_it_build_only_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithBuildOnlyAppTests());
        engine.Repo.Replace("App/Report.cs", "\"value=\"", "\"value: \"");

        var selection = await SelectAsync(engine, "App/Report.cs");

        Assert.Equal("App changed and runs behind an application host", Assert.IsType<TestSelection.Whole>(selection["App.Tests"]).Reason);
    }

    [Fact]
    public async Task A_change_an_application_uses_selects_a_test_project_that_references_it_build_only_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithBuildOnlyAppTests());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        var selection = await SelectAsync(engine, "Lib/Calc.cs");

        Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
    }

    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    /// <summary>The standard repository with App.Tests declaring App build-only and naming no App type, as a test that starts App as a child process does.</summary>
    private static FixtureRepo WithBuildOnlyAppTests()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("App.Tests/App.Tests.csproj", """<ProjectReference Include="../App/App.csproj" />""", """<ProjectReference Include="../App/App.csproj" ReferenceOutputAssembly="false" />""");
        repo.Write("App.Tests/ReportTests.cs", """
            namespace App.Tests;

            public class ReportTests
            {
                [Fact]
                public void Formats() => Assert.Equal(5, 5);
            }
            """);
        repo.Commit("declare App build-only in App.Tests");
        return repo;
    }

    private static string LibraryProject() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static string AppProject() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../Lib/Lib.csproj" />
          </ItemGroup>
        </Project>
        """;

    private static string TestProject(string reference) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
          <ItemGroup>
            {reference}
          </ItemGroup>
        </Project>
        """;
}
