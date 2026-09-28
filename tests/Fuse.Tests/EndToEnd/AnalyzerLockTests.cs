using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A repository that builds its own source generator, referenced with <c>OutputItemType="Analyzer"</c>. The engine
///     loads that generator to check the project that uses it, and it lives for 30 idle minutes, so if it loaded the
///     generator from the generator project's <c>bin</c> folder, the next real build would fail to copy the rebuilt
///     generator there.
/// </summary>
public class AnalyzerLockTests
{
    [Fact]
    public async Task A_warm_engine_does_not_stop_a_build_from_replacing_an_in_repository_generator()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Directory.Build.props"] = "<Project />\n",
            ["Directory.Build.targets"] = "<Project />\n",
            ["Gen/Gen.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.0</TargetFramework>
                    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
                    <IsRoslynComponent>true</IsRoslynComponent>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """,
            ["Gen/Gen.cs"] = """
                using Microsoft.CodeAnalysis;

                [Generator]
                public sealed class Gen : IIncrementalGenerator
                {
                    public void Initialize(IncrementalGeneratorInitializationContext context) =>
                        context.RegisterPostInitializationOutput(c => c.AddSource("G.g.cs", "namespace App { public static class Generated { public static int V => 1; } }"));
                }
                """,
            ["App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                  </ItemGroup>
                </Project>
                """,
            ["App/Use.cs"] = "namespace App;\n\npublic static class Use\n{\n    public static int Run() => Generated.V;\n}\n",
        });
        try
        {
            FixtureRepo.Run(repo.Path, "dotnet", "build", "App/App.csproj", "-nologo", "-v:q");

            // The check loads App, and with it the generator; the error proves the generated type resolved.
            repo.Replace("App/Use.cs", "Generated.V;", "Generated.V + missing;");
            var check = await FuseProcess.RunAsync(repo.Path, null, "check", "App/Use.cs");
            Assert.Equal(1, check.ExitCode);
            Assert.Contains("CS0103", check.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Generated", check.Stdout, StringComparison.Ordinal);

            // Rebuilding the generator while the engine is warm has to be able to overwrite its output.
            repo.Replace("App/Use.cs", "Generated.V + missing;", "Generated.V;");
            repo.Replace("Gen/Gen.cs", "V => 1", "V => 2");
            var build = await FuseProcess.RunAsync(repo.Path, null, "build", "App/App.csproj");
            Assert.DoesNotContain("MSB3021", build.Stdout + build.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("MSB3027", build.Stdout + build.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, build.ExitCode);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
