using System.Collections.Immutable;
using Fuse.Check.Analyzers;
using Fuse.Check.Diagnostics;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
namespace Fuse.Tests.Engine;

/// <summary>
///     On a large project the engine spent minutes in analyzers. A compilation start analyzer ran once per file
///     a whole project bound, the client's wait gave the analyzers minutes, and the compilation-wide threshold
///     kept whole-project readers running per file.
/// </summary>
public class AnalyzerBudgetTests
{
    [Fact]
    public async Task A_whole_project_runs_its_compilation_start_once()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new BudgetStartAnalyzer(TimeSpan.FromMilliseconds(250));
        var solution = SolutionWith(repo, slow, files: 5);
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var cancellationToken = TestContext.Current.CancellationToken;

        collector.Begin(AnalyzerRun.Full, reuse: false);
        var errors = await collector.ForProjectAsync(solution.Projects.Single(), cancellationToken);

        Assert.Equal(5, errors.Count);
        Assert.All(errors, e => Assert.Equal("BUDG1", e.Id));
        Assert.Equal(
            ["C0.cs", "C1.cs", "C2.cs", "C3.cs", "C4.cs"],
            errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(1, slow.Starts);
    }

    [Fact]
    public async Task A_start_above_a_hundred_and_fifty_milliseconds_runs_once_per_compilation()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new BudgetStartAnalyzer(TimeSpan.FromMilliseconds(250));
        var solution = SolutionWith(repo, slow, files: 3);
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var cancellationToken = TestContext.Current.CancellationToken;

        collector.Begin(AnalyzerRun.Full, reuse: false);
        foreach (var i in new[] { 0, 1, 2 })
            Assert.Equal([($"C{i}.cs", "BUDG1")], (await collector.ForFileAsync(solution, repo.Root.PathOf(repo.Full($"C{i}.cs")), cancellationToken)).Select(e => (e.Path, e.Id)));

        Assert.Equal(5, slow.Starts);
    }

    [Fact]
    public void A_six_hundred_second_wait_gives_a_sixty_second_analyzer_budget()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), AnalyzerRun.BudgetFor(TimeSpan.FromSeconds(600)));
        Assert.Equal(TimeSpan.FromSeconds(8), AnalyzerRun.BudgetFor(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(60), AnalyzerRun.MaxBudget);
    }

    [Fact]
    public void The_compilation_wide_threshold_is_a_hundred_and_fifty_milliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), AnalyzerPlan.CompilationWideStart);
    }

    private static Solution SolutionWith(FixtureRepo repo, DiagnosticAnalyzer analyzer, int files)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([analyzer])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        for (var i = 0; i < files; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), $"C{i}.cs", $"public class C{i} {{ }}\n", filePath: repo.Full($"C{i}.cs"));
        return solution;
    }

    /// <summary>Reports every class, after a compilation start that stands in for reading the whole compilation.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class BudgetStartAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("BUDG1", "Budget", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        private readonly TimeSpan _start;
        private int _starts;

        public BudgetStartAnalyzer(TimeSpan start) => _start = start;

        public int Starts => Volatile.Read(ref _starts);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                Interlocked.Increment(ref _starts);
                Thread.Sleep(_start);
                start.RegisterSymbolAction(s => s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name)), SymbolKind.NamedType);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
