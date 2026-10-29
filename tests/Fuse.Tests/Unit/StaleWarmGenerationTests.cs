using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>A baseline warm that started before HEAD moved again must not clear what the newer baseline cached.</summary>
public class StaleWarmGenerationTests
{
    [Fact]
    public async Task A_warm_for_an_older_baseline_generation_is_skipped()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var solution = SolutionWith(repo);
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C1.cs"));
        var cancellationToken = TestContext.Current.CancellationToken;

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var (errors, finished) = await collector.ForBaselineFileAsync(solution, 3, path, cancellationToken);

        Assert.True(finished);
        Assert.Contains(errors, e => e.Id == "WARM1");

        Assert.False(await collector.WarmBaselineProjectAsync(solution.Projects.Single(), 2, cancellationToken));

        var (again, finishedAgain) = await collector.ForBaselineFileAsync(solution, 3, path, cancellationToken);
        Assert.True(finishedAgain);
        Assert.Equal(errors, again);
    }

    private static Solution SolutionWith(FixtureRepo repo)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([new WarmAnalyzer()])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C0.cs", "public class C0 { }\n", filePath: repo.Full("C0.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C1.cs", "public class C1 { }\n", filePath: repo.Full("C1.cs"));
        return solution;
    }

    /// <summary>Reports every class, after a compilation start slow enough to plan it whole.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class WarmAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("WARM1", "Warm", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                Thread.Sleep(AnalyzerPlan.CompilationWideStart * 1.5);
                start.RegisterSymbolAction(
                    s => s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name)),
                    SymbolKind.NamedType);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
