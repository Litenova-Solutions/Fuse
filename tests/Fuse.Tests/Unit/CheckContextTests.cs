using System.Collections.Immutable;
using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     What one check holds for itself stays its own: how it runs analyzers, whether it reuses errors, and the
///     counts and reports it takes while it binds. Two contexts used at the same time never overwrite or steal
///     each other's, and a run a pinned context waits on is not stopped by a newer compilation until the pin goes.
/// </summary>
public class CheckContextTests
{
    [Fact]
    public void Contexts_keep_their_runs_and_reuse_apart()
    {
        using var deadline = new CancellationTokenSource();
        var limited = new CheckContext(new AnalyzerRun(CompilationWide: false, deadline.Token), reuse: false);
        var full = new CheckContext(AnalyzerRun.Full, reuse: true);

        Assert.False(limited.Run.CompilationWide);
        Assert.Equal(deadline.Token, limited.Run.Deadline);
        Assert.False(limited.Reuse);
        Assert.True(full.Run.CompilationWide);
        Assert.True(full.Reuse);
    }

    [Fact]
    public void Contexts_keep_their_reports_and_counts_apart()
    {
        var first = new CheckContext(AnalyzerRun.Full, reuse: true);
        var second = new CheckContext(AnalyzerRun.Full, reuse: true);

        first.MarkUnfinished("Lib/Calc.cs");
        first.MarkLeftOut("Example/SlowAnalyzer");
        first.CountReused();
        first.CountReused();
        first.CountStoreLookup();
        first.CountStoreHit();
        first.CountDocumentBound();

        var (unfinished, leftOut) = first.TakeAnalyzerReport();
        Assert.Equal(1, unfinished);
        Assert.Equal(["Example/SlowAnalyzer"], leftOut);
        Assert.Equal(0, second.TakeAnalyzerReport().Unfinished);
        Assert.Empty(second.TakeAnalyzerReport().LeftOut);
        Assert.Equal(2, first.TakeReused());
        Assert.Equal(0, second.TakeReused());
        Assert.Equal((1, 0), first.TakeBaselineReport());
        Assert.Equal((0, 0), second.TakeBaselineReport());
        // A report is taken once: a second call for the same check answers empty.
        Assert.Equal(0, first.TakeAnalyzerReport().Unfinished);
    }

    [Fact]
    public void Contexts_keep_their_timings_apart()
    {
        var first = new CheckContext(AnalyzerRun.Full, reuse: false);
        var second = new CheckContext(AnalyzerRun.Full, reuse: false);

        first.AddCompilerTicks(System.Diagnostics.Stopwatch.Frequency);
        first.AddAnalyzerTicks(System.Diagnostics.Stopwatch.Frequency / 2);
        first.AddUpdateTicks(System.Diagnostics.Stopwatch.Frequency / 4);

        Assert.Equal((0L, 0L, 0L, 0), second.TakeBindParts());
        var parts = first.TakeBindParts();
        Assert.Equal(250, parts.UpdateMs);
        Assert.Equal(1000, parts.CompilerMs);
        Assert.Equal(500, parts.AnalyzerMs);
    }

    [Fact]
    public async Task A_collector_counts_each_concurrent_contexts_unfinished_files_on_its_own_context()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var solution = SolutionWith(repo, new CheapAnalyzer());
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var path = repo.Root.PathOf(repo.Full("C1.cs"));
        using var passed = new CancellationTokenSource();
        await passed.CancelAsync();
        var limited = new CheckContext(new AnalyzerRun(CompilationWide: true, passed.Token), reuse: false);
        var full = new CheckContext(AnalyzerRun.Full, reuse: false);
        collector.BeginContext(limited);
        collector.BeginContext(full);

        var limitedRun = collector.ForFileAsync(solution, path, limited, TestContext.Current.CancellationToken);
        var fullRun = collector.ForFileAsync(solution, path, full, TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(limitedRun, fullRun);

        Assert.DoesNotContain(results[0], e => e.Id == "CHEAP1");
        Assert.Contains(results[1], e => e.Id == "CHEAP1");
        Assert.Equal(1, limited.TakeAnalyzerReport().Unfinished);
        Assert.Equal(0, full.TakeAnalyzerReport().Unfinished);
    }

    [Fact]
    public async Task A_newer_compilation_does_not_stop_a_run_a_pinned_context_waits_on_until_the_pin_goes()
    {
        var analyses = new CompilationAnalyses();
        var project = ProjectId.CreateNewId();
        var context = new CheckContext(AnalyzerRun.Full, reuse: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = new CancellationTokenSource();

        var pinned = analyses.RunAsync(project, baseline: false, NewCompilation(), everyAnalyzer: false, async stop =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, stop);
            }
            catch (OperationCanceledException)
            {
                stopped.TrySetResult();
                throw;
            }

            return [];
        }, context, wait.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var newer = await analyses.RunAsync(project, baseline: false, NewCompilation(), everyAnalyzer: false, _ => Task.FromResult(ImmutableArray<Diagnostic>.Empty), TestContext.Current.CancellationToken);

        Assert.Empty(newer);
        Assert.False(pinned.IsCompleted);
        await wait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pinned);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static Solution SolutionWith(FixtureRepo repo, params DiagnosticAnalyzer[] analyzers)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([.. analyzers])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        for (var i = 0; i < 3; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), $"C{i}.cs", $"public class C{i} {{ }}\n", filePath: repo.Full($"C{i}.cs"));
        return solution;
    }

    private static CompilationWithAnalyzers NewCompilation() =>
        CSharpCompilation.Create("P", [CSharpSyntaxTree.ParseText("class C { }", cancellationToken: TestContext.Current.CancellationToken)])
            .WithAnalyzers([new NoAnalyzer()]);

    /// <summary>Reports every class, with no compilation start.</summary>
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

    /// <summary>An analyzer that reports nothing, since a compilation with analyzers needs one.</summary>
    private sealed class NoAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

        public override void Initialize(AnalysisContext context)
        {
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
