using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     An edit inside an #if branch that only one of a library's frameworks compiles still selects the tests of
///     the library, whichever branch it is in. Parsed with the other framework's symbols, the edited line is inactive
///     and the edit looks like no change.
/// </summary>
public class InactiveBranchSelectionTests
{
    [Theory]
    [InlineData("tenth-variant")]
    [InlineData("eighth-variant")]
    public async Task Edit_in_a_branch_one_framework_compiles_selects_its_tests(string literal)
    {
        using var repo = FixtureRepo.CreateEmpty(Files());
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", "Fixture.slnx", "-nologo", "-v:q");
        await using var engine = await InProcessEngine.StartAsync(repo);
        // A warm engine holds the library loaded, so selection reads its document rather than the disk.
        await engine.CheckAsync("Lib/Marker.cs");
        engine.Repo.Replace("Lib/Marker.cs", $"\"{literal}\"", $"\"{literal}!\"");
        await engine.Workspace.SyncAsync([engine.Repo.PathOf("Lib/Marker.cs")], TestContext.Current.CancellationToken);

        var result = await engine.Selector.SelectAsync([engine.Repo.PathOf("Lib/Marker.cs")], TestContext.Current.CancellationToken);

        var selection = Assert.Single(result.Selections);
        Assert.Equal("Lib.Tests", Path.GetFileNameWithoutExtension(selection.Key.Absolute));
        Assert.DoesNotContain(engine.Log, line => line.Contains("syntax diff empty", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Files() => new Dictionary<string, string>
    {
        [".gitignore"] = "bin/\nobj/\n",
        ["Fixture.slnx"] = """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="Lib.Tests/Lib.Tests.csproj" />
            </Solution>
            """,
        ["Lib/Lib.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """,
        ["Lib/Marker.cs"] = """
            namespace Lib;

            public static class Marker
            {
                public static string Which()
                {
            #if NET10_0_OR_GREATER
                    return "tenth-variant";
            #else
                    return "eighth-variant";
            #endif
                }
            }
            """,
        ["Lib.Tests/Lib.Tests.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <Using Include="Xunit" />
              </ItemGroup>
            </Project>
            """,
        ["Lib.Tests/MarkerTests.cs"] = """
            using Lib;

            namespace Lib.Tests;

            public class MarkerTests
            {
                [Fact]
                public void Names_its_variant() => Assert.Equal("eighth-variant", Marker.Which());
            }
            """,
    };
}
