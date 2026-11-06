using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     The analyzer state of a compilation is made once and shared by the checks that bind it: one wrapper and
///     one plan per project serve every check of the compilation, one analyzer driver runs over a file, and a
///     binding context with reuse on shares the check's own counts.
/// </summary>
public class AnalyzerReuseTests
{
    [Fact]
    public async Task Two_checks_of_one_compilation_share_its_wrapper_and_its_plan()
    {
        var analyzer = new CheapAnalyzer();
        var solution = SolutionWith(analyzer);
        var project = solution.Projects.Single();
        var selector = new AnalyzerSelector(() => 0, _ => { });
        var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(compilation);

        var first = selector.For(project, compilation);
        var second = selector.For(project, compilation);

        Assert.NotNull(first);
        Assert.Same(first, second);
        var documents = project.Documents.ToList();
        var firstModel = await documents[0].GetSemanticModelAsync(TestContext.Current.CancellationToken);
        var secondModel = await documents[1].GetSemanticModelAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(firstModel);
        Assert.NotNull(secondModel);
        var firstPlan = await selector.PlanAsync(project, first, firstModel, TestContext.Current.CancellationToken);
        var secondPlan = await selector.PlanAsync(project, first, secondModel, TestContext.Current.CancellationToken);

        Assert.Same(firstPlan, secondPlan);
        Assert.Single(firstPlan.PerFile);
    }

    [Fact]
    public async Task One_file_is_run_under_one_analyzer_driver_a_check()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var analyzer = new StartingAnalyzer();
        var solution = SolutionWith(repo, analyzer);
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C1.cs"));
        var warm = new CheckContext(AnalyzerRun.Full, reuse: false);
        collector.BeginContext(warm);
        // The first bind also makes the project's plan, whose probes run the analyzers of their own.
        await collector.ForFileAsync(solution, path, warm, TestContext.Current.CancellationToken);
        analyzer.ResetStarts();

        var context = new CheckContext(AnalyzerRun.Full, reuse: false);
        var errors = await collector.ForFileAsync(solution, path, context, TestContext.Current.CancellationToken);

        Assert.Contains(errors, e => e.Id == "START1");
        Assert.Equal(1, analyzer.Starts);
    }

    [Fact]
    public void A_binding_context_with_reuse_shares_the_checks_counts()
    {
        var context = new CheckContext(AnalyzerRun.Full, reuse: false);
        var binding = context.WithReuse();

        Assert.True(binding.Reuse);
        Assert.False(context.Reuse);
        binding.CountReused();
        binding.CountDocumentBound();
        Assert.Equal(1, context.TakeReused());
        var shared = new CheckContext(AnalyzerRun.Full, reuse: true);
        Assert.Same(shared, shared.WithReuse());
    }

    private static Solution SolutionWith(params DiagnosticAnalyzer[] analyzers) =>
        SolutionWith(null, analyzers);

    private static Solution SolutionWith(FixtureRepo? repo, params DiagnosticAnalyzer[] analyzers)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([.. analyzers])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        for (var i = 0; i < 3; i++)
        {
            var filePath = repo?.Full($"C{i}.cs") ?? Path.Combine(Path.GetTempPath(), $"C{i}.cs");
            solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), $"C{i}.cs", $"public class C{i} {{ }}\n", filePath: filePath);
        }

        return solution;
    }

    /// <summary>Reports every class, with no compilation start, so it stays a per-file analyzer.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class CheapAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("CHEAP1", "Cheap", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(s => s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name)), SymbolKind.NamedType);
        }
    }

    /// <summary>Reports every class from a compilation start action and counts the drivers that started it.</summary>
    private sealed class StartingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("START1", "Starting", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        private int _starts;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public int Starts => Volatile.Read(ref _starts);

        public void ResetStarts() => Interlocked.Exchange(ref _starts, 0);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                Interlocked.Increment(ref _starts);
                start.RegisterSymbolAction(s => s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name)), SymbolKind.NamedType);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
