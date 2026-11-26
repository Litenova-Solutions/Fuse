using System.Text;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A library built for several frameworks, none of them the test project's, shadows the nearest one the tests
///     load: the net9.0 tests of a net10.0 and net8.0 library run against its net8.0 build, the way MSBuild resolves
///     the reference. A higher build in the shadow fails to load on the tests' runtime.
/// </summary>
public class ShadowNearestVariantTests
{
    [Fact]
    public async Task A_shadow_run_emits_the_nearest_library_variant()
    {
        using var repo = FixtureRepo.CreateEmpty(Files());
        FixtureRepo.Run(repo.Root.Path, "dotnet", "build", "Fixture.slnx", "-nologo", "-v:q");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.IsType<RunMode.Shadow>(run.Mode);
        var lib = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(((RunMode.Shadow)run.Mode).Assembly)!, "Lib.dll"));
        Assert.True(HoldsString(lib, "eighth-variant"), "the shadow Lib.dll does not hold the net8.0 build");
        Assert.False(HoldsString(lib, "tenth-variant"), "the shadow Lib.dll holds the net10.0 build the net9.0 tests cannot load");
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
        ["Lib/Calc.cs"] = """
            namespace Lib;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }

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
        ["Lib.Tests/CalcTests.cs"] = """
            using Lib;

            namespace Lib.Tests;

            public class CalcTests
            {
                [Fact]
                public void Adds() => Assert.Equal(2, new Calc().Add(1, 1));
            }
            """,
    };

    /// <summary>Whether an assembly holds <paramref name="text"/> as a string literal, which the metadata stores in UTF-16.</summary>
    private static bool HoldsString(byte[] assembly, string text) =>
        assembly.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
}
