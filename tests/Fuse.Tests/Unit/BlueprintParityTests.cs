using Fuse.Graph;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Tests.Unit;

public class BlueprintParityTests
{
    // The same design-time properties BlueprintEvaluator extracts with.
    private static readonly Dictionary<string, string> DesignTimeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DesignTimeBuild"] = "true",
        ["BuildingProject"] = "false",
        ["SkipCompilerExecution"] = "true",
        ["ProvideCommandLineArgs"] = "true",
        ["DisableMsCoverageReferencedPathMaps"] = "true",
    };

    [Fact]
    public async Task Blueprint_load_matches_MSBuildWorkspace_on_editorconfig_override_and_analyzer_package()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var root = engine.Repo.Root;
        var graph = await RepoGraph.EvaluateAsync(root, cancellationToken);
        // Strict carries an .editorconfig raising CA1825 to error; App.Tests carries analyzers from its xunit packages.
        var strict = graph.Projects.Single(p => p.Name == "Strict");
        var tests = graph.Projects.Single(p => p.Name == "App.Tests");

        MsBuildSetup.EnsureRegistered();
        Solution msbuildSolution;
        using (var workspace = MSBuildWorkspace.Create(DesignTimeProperties))
        {
            workspace.LoadMetadataForReferencedProjects = false;
            workspace.SkipUnrecognizedProjects = true;
            await workspace.OpenProjectAsync(strict.Path.Absolute, cancellationToken: cancellationToken);
            await workspace.OpenProjectAsync(tests.Path.Absolute, cancellationToken: cancellationToken);
            msbuildSolution = workspace.CurrentSolution;
        }

        var outcome = await BlueprintEvaluator.EnsureProjectsAsync(root, graph, [strict, tests], _ => { }, cancellationToken);
        Assert.Empty(outcome.Failures);
        Assert.NotNull(outcome.Blueprint);
        using var blueprintWorkspace = new BlueprintWorkspace();
        blueprintWorkspace.LoadProjects(root, graph, outcome.Blueprint, [strict, tests], head: null);
        var blueprintSolution = blueprintWorkspace.Solution;

        AssertParity(msbuildSolution, blueprintSolution, "Strict");
        AssertParity(msbuildSolution, blueprintSolution, "App.Tests");

        // Guards: the fixture really exercises an editorconfig severity override and a package analyzer.
        var strictMsbuild = msbuildSolution.Projects.Single(p => p.Name == "Strict");
        Assert.Contains(
            strictMsbuild.AnalyzerConfigDocuments.Select(d => d.FilePath ?? "<null>"),
            f => f.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ReportDiagnostic.Error, await EffectiveSeverityAsync(strictMsbuild, "CA1825", cancellationToken));
        var strictBlueprint = blueprintSolution.Projects.Single(p => p.Name == "Strict");
        Assert.Equal(ReportDiagnostic.Error, await EffectiveSeverityAsync(strictBlueprint, "CA1825", cancellationToken));
        var testsMsbuild = msbuildSolution.Projects.Single(p => p.Name == "App.Tests");
        Assert.Contains(
            testsMsbuild.AnalyzerReferences.OfType<AnalyzerFileReference>(),
            a => a.FullPath is not null && a.FullPath.Contains(".nuget", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertParity(Solution msbuildSolution, Solution blueprintSolution, string name)
    {
        var msbuild = msbuildSolution.Projects.Single(p => p.Name == name);
        var blueprint = blueprintSolution.Projects.Single(p => p.Name == name);

        Assert.Equal(
            AnalyzerPaths(msbuild),
            AnalyzerPaths(blueprint));
        Assert.Equal(
            msbuild.AnalyzerConfigDocuments.Select(d => d.FilePath).Order(StringComparer.Ordinal).ToList(),
            blueprint.AnalyzerConfigDocuments.Select(d => d.FilePath).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            msbuild.AdditionalDocuments.Select(d => d.FilePath).Order(StringComparer.Ordinal).ToList(),
            blueprint.AdditionalDocuments.Select(d => d.FilePath).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            msbuild.Documents.Select(d => d.FilePath).Order(StringComparer.Ordinal).ToList(),
            blueprint.Documents.Select(d => d.FilePath).Order(StringComparer.Ordinal).ToList());

        var msbuildParse = Assert.IsType<CSharpParseOptions>(msbuild.ParseOptions);
        var blueprintParse = Assert.IsType<CSharpParseOptions>(blueprint.ParseOptions);
        Assert.Equal(msbuildParse.LanguageVersion, blueprintParse.LanguageVersion);
        Assert.Equal(msbuildParse.DocumentationMode, blueprintParse.DocumentationMode);
        Assert.Equal(
            msbuildParse.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToList(),
            blueprintParse.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToList());

        var msbuildOptions = Assert.IsType<CSharpCompilationOptions>(msbuild.CompilationOptions);
        var blueprintOptions = Assert.IsType<CSharpCompilationOptions>(blueprint.CompilationOptions);
        Assert.Equal(msbuildOptions.SpecificDiagnosticOptions, blueprintOptions.SpecificDiagnosticOptions);
        Assert.Equal(msbuildOptions.GeneralDiagnosticOption, blueprintOptions.GeneralDiagnosticOption);
        Assert.Equal(msbuildOptions.NullableContextOptions, blueprintOptions.NullableContextOptions);
        Assert.Equal(msbuildOptions.ReportSuppressedDiagnostics, blueprintOptions.ReportSuppressedDiagnostics);
        Assert.Equal(msbuildOptions.OutputKind, blueprintOptions.OutputKind);
        Assert.Equal(msbuildOptions.Platform, blueprintOptions.Platform);
        Assert.Equal(msbuildOptions.OptimizationLevel, blueprintOptions.OptimizationLevel);
        Assert.Equal(msbuildOptions.ModuleName, blueprintOptions.ModuleName);

        Assert.Equal(
            ReferencePaths(msbuild),
            ReferencePaths(blueprint));
        Assert.Equal(msbuild.ProjectReferences.Count(), blueprint.ProjectReferences.Count());
    }

    private static List<string> AnalyzerPaths(Project project) =>
        project.AnalyzerReferences.OfType<AnalyzerFileReference>()
            .Select(a => a.FullPath ?? "<null>")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> ReferencePaths(Project project) =>
        project.MetadataReferences
            .Select(r => (r as PortableExecutableReference)?.FilePath ?? r.Display ?? "<null>")
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     The effective severity of <paramref name="diagnosticId"/> for a top-level source of <paramref name="project"/>
    ///     (top-level so the fixture's <c>[*.cs]</c> section matches it: its <c>*</c> crosses no directory
    ///     separator). Severity entries are not visible through <see cref="AnalyzerConfigOptions"/>; the compiler
    ///     exposes them through <see cref="SyntaxTreeOptionsProvider"/>.
    /// </summary>
    private static async Task<ReportDiagnostic?> EffectiveSeverityAsync(Project project, string diagnosticId, CancellationToken cancellationToken)
    {
        var doc = project.Documents.First(d => d.FilePath is not null && !d.FilePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var tree = await doc.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await project.GetCompilationAsync(cancellationToken);
        var provider = compilation?.Options.SyntaxTreeOptionsProvider;
        if (tree is null || provider is null)
            return null;
        return provider.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out var severity) ? severity : null;
    }
}
