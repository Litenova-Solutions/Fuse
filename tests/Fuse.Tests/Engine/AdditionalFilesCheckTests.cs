using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     An edit to an MSBuild AdditionalFiles item an analyzer reads reaches the analyzers without a restart:
///     removing the entry the analyzer requires introduces its error, and restoring the entry clears it.
/// </summary>
public class AdditionalFilesCheckTests
{
    [Fact]
    public async Task Removing_a_required_entry_from_an_additional_file_introduces_an_error_and_restoring_it_clears_it()
    {
        using var repo = FixtureRepo.CreateEmpty(Files());
        FixtureRepo.Run(repo.Path, "dotnet", "build", "Lib/Lib.csproj", "-nologo", "-v:q");
        await using var engine = await InProcessEngine.StartAsync(repo);

        Assert.Empty((await engine.CheckAllAsync()).Errors);

        repo.Write("Lib/rules.txt", "other\n");
        var broken = await engine.CheckAllAsync();
        Assert.True(broken.Errors.Count == 1, $"expected one error, got {broken.Errors.Count}; engine log: {string.Join(" | ", engine.Log)}");
        var error = Assert.Single(broken.Errors).Error;
        Assert.Equal("Lib/Calc.cs", error.Path);
        Assert.Equal(3, error.Line);
        Assert.Equal("RULE001", error.Id);
        Assert.True(error.FromAnalyzer);
        Assert.Contains("rules.txt", error.Message, StringComparison.Ordinal);

        repo.Write("Lib/rules.txt", "RequiredRule\n");
        Assert.Empty((await engine.CheckAllAsync()).Errors);
    }

    /// <summary>A library with a rules file its analyzer requires an entry in, and the analyzer that requires it.</summary>
    private static Dictionary<string, string> Files() => new Dictionary<string, string>
    {
        [".gitignore"] = "bin/\nobj/\n",
        ["RulesAnalyzer/RulesAnalyzer.csproj"] = """
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
        ["RulesAnalyzer/RulesAnalyzer.cs"] = """
            using System.Collections.Immutable;
            using System.Linq;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.Diagnostics;

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public sealed class RulesAnalyzer : DiagnosticAnalyzer
            {
                private static readonly DiagnosticDescriptor MissingEntry = new DiagnosticDescriptor(
                    "RULE001",
                    "Missing rule entry",
                    "rules.txt lacks the required entry",
                    "Rules",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true);

                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
                {
                    get { return ImmutableArray.Create(MissingEntry); }
                }

                public override void Initialize(AnalysisContext context)
                {
                    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                    context.EnableConcurrentExecution();
                    context.RegisterCompilationStartAction(start =>
                    {
                        var rules = start.Options.AdditionalFiles.FirstOrDefault(f => f.Path.EndsWith("rules.txt"));
                        if (rules == null)
                            return;
                        var text = rules.GetText();
                        if (text != null && text.ToString().Contains("RequiredRule"))
                            return;
                        start.RegisterSymbolAction(symbol =>
                        {
                            var location = symbol.Symbol.Locations.FirstOrDefault();
                            if (location != null && location.IsInSource)
                                symbol.ReportDiagnostic(Diagnostic.Create(MissingEntry, location));
                        }, SymbolKind.NamedType);
                    });
                }
            }
            """,
        ["Lib/Lib.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <AdditionalFiles Include="rules.txt" />
                <ProjectReference Include="../RulesAnalyzer/RulesAnalyzer.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """,
        ["Lib/Calc.cs"] = """
            namespace Lib;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """,
        ["Lib/rules.txt"] = "RequiredRule\n",
    };
}
