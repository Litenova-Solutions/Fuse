using System.Collections.Immutable;
using Fuse.Check;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     An analyzer run over a whole compilation belongs to the compilation, not to the check that started it: a check that
///     stops waiting at its deadline leaves the run going, so the next check of the same compilation takes its result
///     instead of starting again. A newer compilation of the project stops the older one's run.
/// </summary>
public class CompilationAnalysesTests
{
    private static readonly ProjectId Project = ProjectId.CreateNewId();

    [Fact]
    public async Task A_run_goes_on_after_its_check_stops_waiting_and_the_next_check_takes_its_result()
    {
        var analyses = new CompilationAnalyses();
        var compilation = NewCompilation();
        var release = new TaskCompletionSource();
        var calls = 0;
        using var deadline = new CancellationTokenSource();

        var first = analyses.RunAsync(Project, baseline: false, compilation, everyAnalyzer: false, Analyze, deadline.Token);
        await deadline.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        var second = await analyses.RunAsync(Project, baseline: false, compilation, everyAnalyzer: false, Analyze, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Empty(second);
        return;

        async Task<ImmutableArray<Diagnostic>> Analyze(CancellationToken stop)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(stop);
            return [];
        }
    }

    [Fact]
    public async Task A_newer_compilation_of_the_project_stops_the_older_ones_run()
    {
        var analyses = new CompilationAnalyses();

        // The older run would never end on its own, so it ends only when the newer compilation stops it.
        var older = analyses.RunAsync(Project, baseline: false, NewCompilation(), everyAnalyzer: false, async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return [];
        }, TestContext.Current.CancellationToken);
        var newer = await analyses.RunAsync(Project, baseline: false, NewCompilation(), everyAnalyzer: false, _ => Task.FromResult(ImmutableArray<Diagnostic>.Empty), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Empty(newer);
    }

    [Fact]
    public async Task A_check_that_reaches_a_newer_compilation_without_a_run_stops_the_older_ones_run()
    {
        var analyses = new CompilationAnalyses();
        var older = analyses.RunAsync(Project, baseline: false, NewCompilation(), everyAnalyzer: false, async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return [];
        }, TestContext.Current.CancellationToken);

        analyses.StopOlder(Project, baseline: false, NewCompilation());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_check_of_the_same_compilation_leaves_its_run_going()
    {
        var analyses = new CompilationAnalyses();
        var compilation = NewCompilation();
        var release = new TaskCompletionSource();
        var run = analyses.RunAsync(Project, baseline: false, compilation, everyAnalyzer: false, async stop =>
        {
            await release.Task.WaitAsync(stop);
            return [];
        }, TestContext.Current.CancellationToken);

        analyses.StopOlder(Project, baseline: false, compilation);
        release.SetResult();

        Assert.Empty(await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_baseline_and_the_working_tree_of_one_project_run_side_by_side()
    {
        var analyses = new CompilationAnalyses();
        var release = new TaskCompletionSource();

        var baseline = analyses.RunAsync(Project, baseline: true, NewCompilation(), everyAnalyzer: false, async stop =>
        {
            await release.Task.WaitAsync(stop);
            return [];
        }, TestContext.Current.CancellationToken);
        await analyses.RunAsync(Project, baseline: false, NewCompilation(), everyAnalyzer: false, _ => Task.FromResult(ImmutableArray<Diagnostic>.Empty), TestContext.Current.CancellationToken);
        release.SetResult();

        Assert.Empty(await baseline);
    }

    [Fact]
    public async Task A_check_past_its_deadline_starts_no_run()
    {
        var analyses = new CompilationAnalyses();
        using var passed = new CancellationTokenSource();
        await passed.CancelAsync();
        var calls = 0;

        var run = analyses.RunAsync(Project, baseline: false, NewCompilation(), everyAnalyzer: true, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ImmutableArray<Diagnostic>.Empty);
        }, passed.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_stopped_run_starts_again_for_the_next_check()
    {
        var analyses = new CompilationAnalyses();
        var compilation = NewCompilation();
        var startedAgain = false;

        var stopped = analyses.RunAsync(Project, baseline: true, compilation, everyAnalyzer: true, async stop =>
        {
            await Task.Delay(Timeout.Infinite, stop);
            return [];
        }, TestContext.Current.CancellationToken);
        analyses.StopAll(baseline: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var again = await analyses.RunAsync(Project, baseline: true, compilation, everyAnalyzer: true, _ =>
        {
            startedAgain = true;
            return Task.FromResult(ImmutableArray<Diagnostic>.Empty);
        }, TestContext.Current.CancellationToken);

        Assert.True(startedAgain);
        Assert.Empty(again);
    }

    private static CompilationWithAnalyzers NewCompilation() =>
        CSharpCompilation.Create("P", [CSharpSyntaxTree.ParseText("class C { }", cancellationToken: TestContext.Current.CancellationToken)])
            .WithAnalyzers([new NoAnalyzer()]);

    /// <summary>An analyzer that reports nothing, since a compilation with analyzers needs one.</summary>
#pragma warning disable RS1001, RS1025, RS1026
    private sealed class NoAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

        public override void Initialize(AnalysisContext context)
        {
        }
    }
#pragma warning restore RS1001, RS1025, RS1026
}
