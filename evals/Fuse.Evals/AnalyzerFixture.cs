using System.Text;
using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>
///     Creates the repository the analyzers suite measures: an analyzer project whose compilation start binds every
///     invocation in every file, as an analyzer package that reads the whole project does, and an app project of
///     <see cref="Files"/> files that uses it, its rule at error severity. Committed to git and built.
/// </summary>
internal static class AnalyzerFixture
{
    /// <summary>How many source files the app project holds.</summary>
    public const int Files = 300;

    /// <summary>The id of the analyzer's rule, an error on every class whose name starts with Forbidden.</summary>
    public const string RuleId = "SLOW001";

    /// <summary>The analyzer's type name, as the engine log names it.</summary>
    public const string AnalyzerName = "WholeProjectAnalyzer";

    public static async Task<string> CreateAsync(string directory)
    {
        if (Directory.Exists(Path.Combine(directory, ".git")) && File.Exists(Path.Combine(directory, "AnalyzerFixture.sln")))
            return directory;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);

        async Task Run(string file, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(file, args, directory, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed:\n{result.Output}");
        }

        // The fixture lives inside the Fuse repository; these files stop MSBuild from importing Fuse's own build settings.
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(directory, "src", "Slow.Analyzers"));
        Directory.CreateDirectory(Path.Combine(directory, "src", "App", "Parts"));
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "Slow.Analyzers", "Slow.Analyzers.csproj"), AnalyzerProject);
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "Slow.Analyzers", $"{AnalyzerName}.cs"), AnalyzerSource);
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "App", "App.csproj"), AppProject);
        for (var i = 0; i < Files; i++)
            await File.WriteAllTextAsync(Path.Combine(directory, "src", "App", "Parts", $"Part{i}.cs"), Part(i));
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"), "bin/\nobj/\n");

        await Run("git", "init", "-q");
        await Run("dotnet", "new", "sln", "-n", "AnalyzerFixture", "--format", "sln");
        await Run("dotnet", "sln", "AnalyzerFixture.sln", "add", "src/Slow.Analyzers", "src/App");
        await Run("git", "add", "-A");
        await Run("git", "-c", "user.email=evals@fuse.local", "-c", "user.name=fuse evals", "commit", "-qm", "analyzer fixture");
        await Run("dotnet", "build", "AnalyzerFixture.sln", "-nologo", "-v:q");
        return directory;
    }

    /// <summary>The path of part <paramref name="i"/>, relative to the fixture.</summary>
    public static string PartPath(int i) => Path.Combine("src", "App", "Parts", $"Part{i}.cs");

    /// <summary>
    ///     Part <paramref name="i"/>: a class whose methods call the previous part's, so binding one file reaches others,
    ///     with <paramref name="value"/> in one method body so a rewrite with another value is a body edit.
    /// </summary>
    public static string Part(int i, int value = 0)
    {
        var previous = i == 0 ? "" : $"Part{i - 1}.Sum(n) + ";
        var text = new StringBuilder();
        text.Append($"namespace App.Parts;\n\npublic static class Part{i}\n{{\n");
        text.Append($"    public static int Sum(int n)\n    {{\n        var total = {value};\n        for (var k = 0; k < n; k++)\n            total += Twice(k);\n        return {previous}total;\n    }}\n\n");
        text.Append("    public static int Twice(int k) => System.Math.Max(k, 0) * 2;\n\n");
        text.Append("    public static int Count(int n) => System.Linq.Enumerable.Count(System.Linq.Enumerable.Range(0, n), x => Twice(x) > 4);\n");
        text.Append("}\n");
        return text.ToString();
    }

    private const string AnalyzerProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>netstandard2.0</TargetFramework>
            <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
            <IsRoslynComponent>true</IsRoslynComponent>
            <NoWarn>RS1030;RS2008</NoWarn>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
          </ItemGroup>
        </Project>
        """;

    private const string AppProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../Slow.Analyzers/Slow.Analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
          </ItemGroup>
        </Project>
        """;

    // Three passes stand in for a package of several such analyzers, as real ones are.
    private const string AnalyzerSource = """
        using System.Collections.Immutable;
        using System.Linq;
        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.CSharp.Syntax;
        using Microsoft.CodeAnalysis.Diagnostics;

        namespace Slow.Analyzers;

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        public sealed class WholeProjectAnalyzer : DiagnosticAnalyzer
        {
            private static readonly DiagnosticDescriptor Rule = new("SLOW001", "Forbidden type", "Type {0} is forbidden", "Design", DiagnosticSeverity.Error, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

            public override void Initialize(AnalysisContext context)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.EnableConcurrentExecution();
                context.RegisterCompilationStartAction(start =>
                {
                    var bound = 0;
                    for (var pass = 0; pass < 3; pass++)
                    {
                        foreach (var tree in start.Compilation.SyntaxTrees)
                        {
                            var model = start.Compilation.GetSemanticModel(tree);
                            foreach (var invocation in tree.GetRoot(start.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                            {
                                if (model.GetSymbolInfo(invocation, start.CancellationToken).Symbol is not null)
                                    bound++;
                            }
                        }
                    }

                    start.RegisterSymbolAction(
                        symbol =>
                        {
                            if (symbol.Symbol.Name.StartsWith("Forbidden", System.StringComparison.Ordinal))
                                symbol.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Symbol.Locations[0], symbol.Symbol.Name));
                        },
                        SymbolKind.NamedType);
                });
            }
        }
        """;
}
