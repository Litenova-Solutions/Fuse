using Fuse.Graph;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>Which packages mark a test project as needing a container runtime: Testcontainers, exactly.</summary>
public class InfraDetectionTests
{
    private static async Task<bool> RequiresRuntimeAsync(string packages)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Tests/Tests.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                """ + packages + """
                  </ItemGroup>
                </Project>
                """,
            ["Tests/CalcTests.cs"] = "namespace Tests;\n\npublic class CalcTests\n{\n}\n",
        });
        var graph = await RepoGraph.EvaluateAsync(repo.Root, TestContext.Current.CancellationToken).ConfigureAwait(false);
        return graph.Find(repo.PathOf("Tests/Tests.csproj"))!.RequiresContainerRuntime;
    }

    [Fact]
    public async Task A_testcontainers_package_marks_the_project() =>
        Assert.True(await RequiresRuntimeAsync("""<PackageReference Include="Testcontainers.PostgreSql" Version="3.9.0" />"""));

    [Fact]
    public async Task The_bare_testcontainers_package_marks_the_project() =>
        Assert.True(await RequiresRuntimeAsync("""<PackageReference Include="Testcontainers" Version="3.9.0" />"""));

    [Fact]
    public async Task The_spelling_is_case_insensitive() =>
        Assert.True(await RequiresRuntimeAsync("""<PackageReference Include="testcontainers.postgresql" Version="3.9.0" />"""));

    [Fact]
    public async Task Other_packages_do_not_mark_the_project() =>
        Assert.False(await RequiresRuntimeAsync("""
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="MyTestcontainersHelper" Version="1.0.0" />
            """));
}
