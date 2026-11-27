using System.Collections.Immutable;
using Fuse.Check.Pipeline;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
namespace Fuse.Check.Analyzers;

/// <summary>
///     Runs a file's analyzers: the per-file analyzers on its model and the compilation-wide ones once per
///     compilation, shared by every file of a check through <see cref="CompilationAnalyses"/>. A run past the
///     check's deadline keeps the errors found so far, and a working-tree file counts as unfinished.
/// </summary>
internal sealed class AnalyzerExecution
{
    private readonly AnalyzerSelector _analyzers;
    private readonly CompilationAnalyses _analyses;
    private readonly RepoRoot _root;

    /// <param name="analyzers">Picks the analyzers that can report an error and plans their runs.</param>
    /// <param name="analyses">The shared compilation-wide analyzer runs.</param>
    /// <param name="root">The repository, for relative paths.</param>
    public AnalyzerExecution(AnalyzerSelector analyzers, CompilationAnalyses analyses, RepoRoot root)
    {
        _analyzers = analyzers;
        _analyses = analyses;
        _root = root;
    }

    /// <summary>
    ///     The analyzer errors in the file of <paramref name="request"/>'s model: the per-file analyzers run on that file when
    ///     the request asks for them, and the compilation-wide ones once over the whole compilation, whose result
    ///     every file of the check shares. The baseline runs the same analyzers as the working tree. Past the check's
    ///     deadline, the file keeps the analyzer errors found so far, and a working-tree file counts as unfinished.
    /// </summary>
    /// <returns>
    ///     Each error with whether a per-file analyzer reported it, whether the per-file analyzers finished, and whether
    ///     every analyzer the check runs finished.
    /// </returns>
    internal async Task<(List<(Diagnostic Diagnostic, bool PerFile)> Errors, bool PerFileFinished, bool Finished)> RunAsync(
        AnalyzerExecutionRequest request, CancellationToken cancellationToken)
    {
        var withAnalyzers = _analyzers.For(request.Project, request.Model.Compilation);
        if (withAnalyzers is null)
            return ([], true, true);
        var run = request.Context.Run;
        var result = new List<(Diagnostic Diagnostic, bool PerFile)>();
        var perFileFinished = false;
        var finished = false;
        using var analyzing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Deadline);
        try
        {
            // The per-file part is timed here; the compilation-wide run, which every file waits for, times itself once.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            AnalyzerPlan plan;
            try
            {
                plan = await _analyzers.PlanAsync(request.Project, withAnalyzers, request.Model, analyzing.Token).ConfigureAwait(false);
                if (request.PerFile && !plan.PerFile.IsEmpty)
                {
                    // One analysis of the model, not one of its tree and one of the model: each of the two
                    // starts its own analyzer driver over the file, so the analyzers' compilation start
                    // actions and their driver setup ran twice for every file a check bound.
                    var analysis = await withAnalyzers.GetAnalysisResultAsync(request.Model, filterSpan: null, plan.PerFile, analyzing.Token).ConfigureAwait(false);
                    result.AddRange(analysis.GetAllDiagnostics().Select(d => (d, true)));
                }

                perFileFinished = true;
            }
            finally
            {
                request.Context.AddAnalyzerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }

            if (!plan.CompilationWide.IsEmpty && !run.CompilationWide && !request.WholeProject)
            {
                foreach (var (analyzer, _) in plan.CompilationWide)
                    request.Context.MarkLeftOut(AnalyzerSelector.Name(analyzer));
                // This compilation is the project's newest, so a run an earlier check left going over an older one stops when no pinned check waits on it.
                _analyses.StopOlder(request.Project.Id, request.Baseline, withAnalyzers);
            }
            else if (!plan.CompilationWide.IsEmpty)
            {
                var all = await CompilationWideAsync(request.Project, request.Baseline, withAnalyzers, plan.CompilationWideAnalyzers, request.Context, analyzing.Token).ConfigureAwait(false);
                result.AddRange(all.Where(d => d.Location.SourceTree == request.Model.SyntaxTree).Select(d => (d, false)));
            }

            finished = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!request.Baseline)
                request.Context.MarkUnfinished(request.WholeProject ? request.Project.Name : _root.PathOf(request.Model.SyntaxTree.FilePath).Relative);
        }

        return ([.. result.Where(d => IsError(d.Diagnostic))], perFileFinished, finished);
    }

    /// <summary>
    ///     The diagnostics the compilation-wide analyzers report anywhere in the compilation, computed once per compilation:
    ///     the files of a check, and every later check until an edit makes a new compilation, share it, and it runs on past
    ///     the deadline of the check that started it (<see cref="CompilationAnalyses"/>).
    /// </summary>
    internal Task<ImmutableArray<Diagnostic>> CompilationWideAsync(Project project, bool baseline, CompilationWithAnalyzers analyzers, ImmutableArray<DiagnosticAnalyzer> wide, CheckContext context, CancellationToken wait) =>
        _analyses.RunAsync(
            project.Id,
            baseline,
            analyzers,
            everyAnalyzer: false,
            async stop =>
            {
                var timer = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    return (await analyzers.GetAnalysisResultAsync(wide, stop).ConfigureAwait(false)).GetAllDiagnostics();
                }
                finally
                {
                    context.AddAnalyzerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - timer);
                }
            },
            context,
            wait);

    private static bool IsError(Diagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Error && !diagnostic.IsSuppressed;
}
