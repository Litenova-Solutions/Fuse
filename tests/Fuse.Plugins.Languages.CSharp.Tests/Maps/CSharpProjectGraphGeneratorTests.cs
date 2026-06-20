using Fuse.Plugins.Languages.CSharp.Maps;

namespace Fuse.Plugins.Languages.CSharp.Tests.Maps;

public sealed class CSharpProjectGraphGeneratorTests
{
    private readonly CSharpProjectGraphGenerator _generator = new();

    [Fact]
    public void Generate_CsprojReferences_ProducesEdges()
    {
        var content = new Dictionary<string, string>
        {
            ["src/Web/Web.csproj"] = """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\Core\Core.csproj" />
                  </ItemGroup>
                </Project>
                """
        };

        var result = _generator.Generate(content);

        Assert.Contains("fuse:project-graph", result);
        Assert.Contains("Web -> Core", result);
    }

    [Fact]
    public void Generate_SolutionFile_ListsProjects()
    {
        var content = new Dictionary<string, string>
        {
            ["App.sln"] = """
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Web", "src\Web\Web.csproj", "{GUID}"
                EndProject
                """
        };

        var result = _generator.Generate(content);

        Assert.Contains("solution -> Web", result);
        Assert.Contains("src/Web/Web.csproj", result);
    }
}
