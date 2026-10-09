using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     Roslyn analyzes one file on a fresh copy of the compilation and runs every analyzer's compilation start actions
///     again for each file. An analyzer whose start reads the whole compilation would cost a whole-project pass per file
///     checked, so it runs once per compilation instead, and the check still reports its errors file by file.
/// </summary>
public class AnalyzerPlanTests
{
    private const int Files = 10;

    [Fact]
    public async Task An_analyzer_with_a_slow_start_runs_once_for_every_file_of_a_compilation()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new SlowStartAnalyzer();
        var cheap = new CheapAnalyzer();
        var lines = new List<string>();
        var solution = SolutionWith(repo, slow, cheap);
        var collector = new DiagnosticCollector(repo.Root, () => 0, lines.Add);

        var errors = await Task.WhenAll(Enumerable.Range(0, Files).Select(i =>
            collector.ForFileAsync(solution, repo.Root.PathOf(repo.Full($"C{i}.cs")), TestContext.Current.CancellationToken)));

        // Each file still gets both analyzers' errors for its own class, and no other file's.
        for (var i = 0; i < Files; i++)
            Assert.Equal([($"C{i}.cs", "CHEAP1"), ($"C{i}.cs", "SLOW1")], errors[i].Select(e => (e.Path, e.Id)).Order());
        // File by file it would start twice per file; planned, it starts for the probe's two calls and the one compilation-wide run.
        Assert.Equal(3, slow.Starts);
        var plan = Assert.Single(lines);
        Assert.Contains("1 per file, 1 compilation-wide", plan);
        Assert.Contains(nameof(SlowStartAnalyzer), plan);
    }

    [Fact]
    public async Task Analyzers_with_cheap_starts_run_file_by_file()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var lines = new List<string>();
        var solution = SolutionWith(repo, new CheapAnalyzer());
        var collector = new DiagnosticCollector(repo.Root, () => 0, lines.Add);

        var errors = await collector.ForFileAsync(solution, repo.Root.PathOf(repo.Full("C3.cs")), TestContext.Current.CancellationToken);

        Assert.Equal([("C3.cs", "CHEAP1")], errors.Select(e => (e.Path, e.Id)));
        Assert.Empty(lines);
    }

    [Fact]
    public async Task An_analyzer_whose_start_costs_less_than_a_second_stays_file_by_file()
    {
        // A whole-compilation run binds every file of the project, which costs more than a short start on each checked file.
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var moderate = new SlowStartAnalyzer(TimeSpan.FromMilliseconds(200));
        var lines = new List<string>();
        var solution = SolutionWith(repo, moderate);
        var collector = new DiagnosticCollector(repo.Root, () => 0, lines.Add);

        foreach (var i in new[] { 1, 2 })
        {
            var errors = await collector.ForFileAsync(solution, repo.Root.PathOf(repo.Full($"C{i}.cs")), TestContext.Current.CancellationToken);
            Assert.Equal([($"C{i}.cs", "SLOW1")], errors.Select(e => (e.Path, e.Id)));
        }

        Assert.Empty(lines);
        // The probe's syntax and semantic calls, on a wrapper of its own, then two per-file calls on each file.
        Assert.Equal(6, moderate.Starts);
    }

    [Fact]
    public async Task A_check_that_leaves_out_compilation_wide_analyzers_names_them_and_still_runs_them_at_HEAD()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var solution = SolutionWith(repo, new SlowStartAnalyzer(), new CheapAnalyzer());
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C1.cs"));

        collector.Begin(new AnalyzerRun(CompilationWide: false, CancellationToken.None));
        var current = await collector.ForFileAsync(solution, path, TestContext.Current.CancellationToken);
        // HEAD's errors must all be known, or an error HEAD has would be reported as introduced.
        var head = await collector.ForBaselineFileAsync(solution, 0, path, TestContext.Current.CancellationToken);

        Assert.Equal(["CHEAP1"], current.Select(e => e.Id));
        Assert.Equal(["CHEAP1", "SLOW1"], head.Select(e => e.Id).Order());
        var (unfinished, leftOut) = collector.TakeAnalyzerReport();
        Assert.Equal(0, unfinished);
        Assert.Equal($"{typeof(SlowStartAnalyzer).Assembly.GetName().Name}/{nameof(SlowStartAnalyzer)}", Assert.Single(leftOut));
    }

    [Fact]
    public async Task Analyzers_past_the_deadline_leave_the_compiler_errors_and_count_as_unfinished()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var solution = SolutionWith(repo, new CheapAnalyzer());
        var project = solution.Projects.Single();
        var broken = project.Documents.Single(d => d.Name == "C2.cs").Id;
        solution = solution.WithDocumentText(broken, Microsoft.CodeAnalysis.Text.SourceText.From("public class C2 { int F() => missing; }\n"));
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        using var passed = new CancellationTokenSource();
        await passed.CancelAsync();

        collector.Begin(new AnalyzerRun(CompilationWide: true, passed.Token));
        var errors = await collector.ForFileAsync(solution, repo.Root.PathOf(repo.Full("C2.cs")), TestContext.Current.CancellationToken);

        Assert.Equal(["CS0103"], errors.Select(e => e.Id));
        Assert.Equal(1, collector.TakeAnalyzerReport().Unfinished);
    }

    private static Solution SolutionWith(FixtureRepo repo, params DiagnosticAnalyzer[] analyzers)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([.. analyzers])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        for (var i = 0; i < Files; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), $"C{i}.cs", $"public class C{i} {{ }}\n", filePath: repo.Full($"C{i}.cs"));
        return solution;
    }

    /// <summary>Reports every class, after a compilation start that stands in for reading the whole compilation.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class SlowStartAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("SLOW1", "Slow", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        private readonly TimeSpan _start;
        private int _starts;

        /// <param name="start">How long its compilation start takes; by default half again the compilation-wide threshold.</param>
        public SlowStartAnalyzer(TimeSpan? start = null) => _start = start ?? AnalyzerPlan.CompilationWideStart * 1.5;

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

    /// <summary>Reports every class, with no compilation start.</summary>
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
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
