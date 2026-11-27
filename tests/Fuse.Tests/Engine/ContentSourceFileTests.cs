using Fuse.Graph;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>A .cs file a project ships as content is not a source the project compiles.</summary>
public class ContentSourceFileTests
{
    [Fact]
    public async Task A_cs_file_shipped_as_content_has_no_owner_and_razor_stays_a_source()
    {
        using var repo = WithTemplateContent();
        var graph = await RepoGraph.EvaluateAsync(repo.Root, TestContext.Current.CancellationToken);
        var template = repo.PathOf("Lib/templates/App/Program.cs");
        var view = repo.PathOf("Lib/templates/App/Card.razor");

        Assert.Empty(graph.OwnersOf(template));
        Assert.Equal(["Lib"], graph.NonSourceOwnersOf(template).Select(p => p.Name).Order(StringComparer.Ordinal));
        var lib = Assert.IsType<ProjectNode>(graph.Find(repo.PathOf("Lib/Lib.csproj")));
        Assert.DoesNotContain(template, lib.Sources);
        Assert.Contains(template, lib.NonSources);

        Assert.Contains(view, lib.Sources);
        Assert.DoesNotContain(view, lib.NonSources);
        Assert.Equal(["Lib"], graph.OwnersOf(view).Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Editing_a_cs_file_shipped_as_content_introduces_no_errors()
    {
        await using var engine = await InProcessEngine.StartAsync(WithTemplateContent());
        engine.Repo.Replace("Lib/templates/App/Program.cs", "__NAMESPACE__", "missingMarker");

        var report = await engine.CheckAllAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(0, report.FilesChecked);
    }

    /// <summary>The standard repository with a template folder Lib ships as content instead of compiling.</summary>
    private static FixtureRepo WithTemplateContent()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/templates/App/Program.cs", "namespace Templates.App;\n\npublic static class TemplateProgram\n{\n    public static string Name => __NAMESPACE__;\n}\n");
        repo.Write("Lib/templates/App/Card.razor", "<p>Card</p>\n");
        repo.Replace("Lib/Lib.csproj", "</Project>", "<ItemGroup><Compile Remove=\"templates/**\" /><Content Include=\"templates/**\" /></ItemGroup></Project>");
        repo.Commit("ship templates as content");
        return repo;
    }
}
