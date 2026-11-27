using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     A package or build settings change selects whole the test projects it can affect: a version bump in
///     <c>Directory.Packages.props</c> selects the test projects that reference the package, a <c>.csproj</c> edit
///     selects its test project and its dependent test projects, and a <c>Directory.Build.props</c> edit selects the
///     test projects under its directory. A comment-only edit selects nothing.
/// </summary>
public class PackageChangeSelectionTests
{
    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    [Fact]
    public async Task Package_version_bump_selects_the_test_projects_using_it()
    {
        await using var engine = await InProcessEngine.StartAsync(WithCentralPackages());
        engine.Repo.Replace("Directory.Packages.props", """Include="Example.One" Version="1.0.0""", """Include="Example.One" Version="2.0.0""");
        var selection = await SelectAsync(engine, "Directory.Packages.props");

        Assert.Equal("package Example.One version changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.False(selection.ContainsKey("App.Tests"));
    }

    [Fact]
    public async Task Package_reference_added_to_a_csproj_selects_its_test_project()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib.Tests/Lib.Tests.csproj", "</Project>", """<ItemGroup><PackageReference Include="Example.Added" Version="1.0.0" /></ItemGroup></Project>""");
        var selection = await SelectAsync(engine, "Lib.Tests/Lib.Tests.csproj");

        Assert.Equal("Lib.Tests.csproj changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.False(selection.ContainsKey("App.Tests"));
    }

    [Fact]
    public async Task Build_props_edit_selects_the_test_projects_under_it()
    {
        await using var engine = await InProcessEngine.StartAsync(WithBuildProps());
        engine.Repo.Replace("Directory.Build.props", "<Example>1</Example>", "<Example>2</Example>");
        var selection = await SelectAsync(engine, "Directory.Build.props");

        Assert.Equal("Directory.Build.props changed", Assert.IsType<TestSelection.Whole>(selection["Lib.Tests"]).Reason);
        Assert.Equal("Directory.Build.props changed", Assert.IsType<TestSelection.Whole>(selection["App.Tests"]).Reason);
    }

    [Fact]
    public async Task Comment_only_edit_selects_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync(WithCentralPackages());
        engine.Repo.Replace("Directory.Packages.props", "</ItemGroup>", "<!-- versions stay --></ItemGroup>");
        Assert.Empty(await SelectAsync(engine, "Directory.Packages.props"));
    }

    /// <summary>
    ///     The standard repository with a committed <c>Directory.Packages.props</c> that versions two packages, one
    ///     referenced only by Lib.Tests and one only by App.Tests.
    /// </summary>
    private static FixtureRepo WithCentralPackages()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Directory.Packages.props", """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Example.One" Version="1.0.0" />
                <PackageVersion Include="Example.Two" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        repo.Replace("Lib.Tests/Lib.Tests.csproj", "</Project>", """<ItemGroup><PackageReference Include="Example.One" /></ItemGroup></Project>""");
        repo.Replace("App.Tests/App.Tests.csproj", "</Project>", """<ItemGroup><PackageReference Include="Example.Two" /></ItemGroup></Project>""");
        repo.Commit("manage test package versions centrally");
        return repo;
    }

    /// <summary>The standard repository with a committed <c>Directory.Build.props</c> at the root.</summary>
    private static FixtureRepo WithBuildProps()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <Example>1</Example>
              </PropertyGroup>
            </Project>
            """);
        repo.Commit("add build properties");
        return repo;
    }
}
