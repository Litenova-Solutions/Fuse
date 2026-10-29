using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A multi-target test project classifies across every target framework: a class behind a framework check in any
///     variant still counts, and any doubtful variant keeps the project whole.
/// </summary>
public class MultiTargetApplicationStartTests
{
    [Fact]
    public async Task Classes_behind_framework_checks_in_any_variant_are_selected()
    {
        await using var engine = await InProcessEngine.StartAsync(WithMultiTargetTests());
        engine.Repo.Replace("App/Report.cs", "\"value=\" + value", "\"value: \" + value");
        await engine.Workspace.SyncAsync([engine.Repo.PathOf("App/Report.cs")], TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync([engine.Repo.PathOf("App/Report.cs")], TestContext.Current.CancellationToken);
        var selection = result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);

        Assert.Single(selection);
        var tests = Assert.IsType<TestSelection.Methods>(selection["Tests"]);
        Assert.Contains("Tests.ClassA.", tests.Patterns);
        Assert.Contains("Tests.ClassB.", tests.Patterns);
        Assert.Contains("Tests.ReportTests.Formats", tests.Patterns);
        Assert.Contains(engine.Log, line => line == "test selection: Tests: 2 of 3 test class(es) start App; selecting them whole, the rest by name");
    }

    /// <summary>A repository with an executable calling <c>Report.Line</c> and a test project for net8.0 and net10.0, with one starting class per framework.</summary>
    private static FixtureRepo WithMultiTargetTests()
    {
        var files = new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            ["Multi.slnx"] = """
                <Solution>
                  <Project Path="App/App.csproj" />
                  <Project Path="Tests/Tests.csproj" />
                </Solution>
                """,
            ["App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
            ["App/Program.cs"] = """
                System.Console.WriteLine(App.Report.Line(1));
                public partial class Program
                {
                }
                """,
            ["App/Report.cs"] = """
                namespace App;

                public static class Report
                {
                    public static string Line(int value) => "value=" + value;
                }
                """,
            ["Tests/Tests.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net8.0;net10.0</TargetFrameworks><IsPackable>false</IsPackable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../App/App.csproj" />
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
            ["Tests/Factory.cs"] = """
                namespace Microsoft.AspNetCore.Mvc.Testing
                {
                    public class WebApplicationFactory<TEntryPoint>
                        where TEntryPoint : class
                    {
                    }
                }
                """,
            ["Tests/ClassA.cs"] = """
                #if NET8_0
                using Microsoft.AspNetCore.Mvc.Testing;

                namespace Tests;

                public class ClassA
                {
                    private readonly WebApplicationFactory<App.Program> _factory = null!;

                    [Fact]
                    public void Boots()
                    {
                    }
                }
                #endif
                """,
            ["Tests/ClassB.cs"] = """
                #if NET10_0
                using Microsoft.AspNetCore.Mvc.Testing;

                namespace Tests;

                public class ClassB
                {
                    private readonly WebApplicationFactory<App.Program> _factory = null!;

                    [Fact]
                    public void Boots()
                    {
                    }
                }
                #endif
                """,
            ["Tests/ReportTests.cs"] = """
                namespace Tests;

                public class ReportTests
                {
                    [Fact]
                    public void Formats() => Assert.Equal("value=5", App.Report.Line(5));
                }
                """,
        };
        var repo = FixtureRepo.CreateEmpty(files);
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", "Multi.slnx", "-nologo", "-v:q");
        return repo;
    }
}
