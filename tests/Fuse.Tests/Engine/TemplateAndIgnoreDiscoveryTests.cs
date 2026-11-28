using Fuse.Graph;
using Fuse.Repo.Tracking;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     Project discovery skips template projects, unrestored placeholder projects, and projects matched by
///     <c>.fuseignore</c>: only the real project reaches the graph, each skipped project logs one line, and a
///     <c>.fuseignore</c> edit changes the stored graph key.
/// </summary>
public class TemplateAndIgnoreDiscoveryTests
{
    [Fact]
    public async Task Only_the_real_project_is_in_the_graph()
    {
        using var repo = FixtureRepo.CreateEmpty(Files());
        var graph = await RepoGraph.EvaluateAsync(repo.Root, TestContext.Current.CancellationToken);

        Assert.Single(graph.Projects);
        Assert.Equal("src/Real/Real.csproj", graph.Projects[0].Path.Relative);
    }

    [Fact]
    public async Task Each_skipped_project_logs_one_line()
    {
        using var repo = FixtureRepo.CreateEmpty(Files());
        await RepoGraph.EvaluateAsync(repo.Root, TestContext.Current.CancellationToken);

        var logFile = Path.Combine(repo.Root.StateDirectory, "engine.log");
        Assert.True(File.Exists(logFile));
        var log = File.ReadAllText(logFile);
        Assert.Contains("templates/web/__NAME__.Web.csproj", log, StringComparison.Ordinal);
        Assert.Contains("fixtures/__APP__/__APP__.csproj", log, StringComparison.Ordinal);
        Assert.Contains("samples/Demo/Demo.csproj", log, StringComparison.Ordinal);
        Assert.Equal(3, log.Split('\n').Count(line => line.Contains("skipped", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_probe_is_false_when_only_skipped_projects_remain()
    {
        using var repo = FixtureRepo.CreateEmpty(OnlySkippedFiles());

        Assert.False(await RepoProbe.HasCSharpProjectsAsync(repo.Root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_fuseignore_edit_changes_the_stored_graph_key()
    {
        using var repo = FixtureRepo.CreateEmpty(Files());
        var graph = await RepoGraph.EvaluateAsync(repo.Root, TestContext.Current.CancellationToken);
        var paths = graph.Projects.Select(p => p.Path).ToList();
        Assert.NotNull(GraphStore.TryLoad(repo.Root, paths));

        repo.Write(".fuseignore", "other/**\n");

        Assert.Null(GraphStore.TryLoad(repo.Root, paths));
    }

    private static Dictionary<string, string> Files() => new()
    {
        [".gitignore"] = "bin/\nobj/\n",
        [".fuseignore"] = "samples/**\n",
        ["templates/web/.template.config/template.json"] = "{}\n",
        ["templates/web/__NAME__.Web.csproj"] = MinimalProject(),
        ["templates/web/Class.cs"] = "namespace Web;\n\npublic class WebClass\n{\n}\n",
        ["fixtures/__APP__/__APP__.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\\__APP__.Lib\\__APP__.Lib.csproj" />
              </ItemGroup>
            </Project>
            """,
        ["fixtures/__APP__/Class.cs"] = "namespace Tokenized;\n\npublic class TokenizedClass\n{\n}\n",
        ["samples/Demo/Demo.csproj"] = MinimalProject(),
        ["samples/Demo/Demo.cs"] = "namespace Demo;\n\npublic class Demo\n{\n}\n",
        ["src/Real/Real.csproj"] = MinimalProject(),
        ["src/Real/Real.cs"] = "namespace Real;\n\npublic class Real\n{\n}\n",
    };

    private static Dictionary<string, string> OnlySkippedFiles() => new()
    {
        [".gitignore"] = "bin/\nobj/\n",
        [".fuseignore"] = "samples/**\n",
        ["templates/web/.template.config/template.json"] = "{}\n",
        ["templates/web/__NAME__.Web.csproj"] = MinimalProject(),
        ["templates/web/Class.cs"] = "namespace Web;\n\npublic class WebClass\n{\n}\n",
        ["fixtures/__APP__/__APP__.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\\__APP__.Lib\\__APP__.Lib.csproj" />
              </ItemGroup>
            </Project>
            """,
        ["fixtures/__APP__/Class.cs"] = "namespace Tokenized;\n\npublic class TokenizedClass\n{\n}\n",
        ["samples/Demo/Demo.csproj"] = MinimalProject(),
        ["samples/Demo/Demo.cs"] = "namespace Demo;\n\npublic class Demo\n{\n}\n",
    };

    private static string MinimalProject() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;
}
