using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     The analyzer runs over whole compilations, each one shared by every file and every check that needs it. A run
///     belongs to its compilation, not to the check that started it: when that check's deadline passes or its client stops
///     waiting, the run goes on, so the next check of the same compilation finds it done instead of starting again. A
///     newer compilation of the same project on the same side stops the older one's runs, so at most one compilation per
///     project and side is analyzed at a time.
/// </summary>
internal sealed class CompilationAnalyses
{
    private readonly ConditionalWeakTable<CompilationWithAnalyzers, Runs> _runs = new();
    private readonly Dictionary<(ProjectId Project, bool Baseline), (CompilationWithAnalyzers Analyzers, CancellationTokenSource Stop)> _latest = [];
    private readonly Lock _lock = new();

    /// <summary>
    ///     The diagnostics of the run of <paramref name="analyze"/> over <paramref name="analyzers"/>, started now when no
    ///     run of it is going or done. A run that was stopped or failed is started again.
    /// </summary>
    /// <param name="project">The project the compilation is of.</param>
    /// <param name="baseline">True for a compilation of the baseline, false for one of the current solution or a view of it.</param>
    /// <param name="analyzers">The compilation with its analyzers, which identifies the run.</param>
    /// <param name="everyAnalyzer">True for the run of every analyzer with the compiler's diagnostics, false for the compilation-wide analyzers alone.</param>
    /// <param name="analyze">Runs the analyzers; called at most once per run, with the token that stops it.</param>
    /// <param name="wait">Stops the wait, not the run. Once it is cancelled, no new run starts.</param>
    public Task<ImmutableArray<Diagnostic>> RunAsync(
        ProjectId project, bool baseline, CompilationWithAnalyzers analyzers, bool everyAnalyzer, Func<CancellationToken, Task<ImmutableArray<Diagnostic>>> analyze, CancellationToken wait)
    {
        Task<ImmutableArray<Diagnostic>>? run;
        CancellationTokenSource? older = null;
        lock (_lock)
        {
            var runs = _runs.GetOrCreateValue(analyzers);
            run = everyAnalyzer ? runs.Every : runs.CompilationWide;
            if (run is null || run.IsCanceled || run.IsFaulted)
            {
                // A check past its deadline would not wait for a new run, which would then analyze for nobody.
                if (wait.IsCancellationRequested)
                    return Task.FromCanceled<ImmutableArray<Diagnostic>>(wait);
                var stop = StopFor(project, baseline, analyzers, out older);
                // Task.Run, so the analyzers' synchronous initialization runs outside the lock.
                run = Task.Run(() => analyze(stop), stop);
                if (everyAnalyzer)
                    runs.Every = run;
                else
                    runs.CompilationWide = run;
            }
        }

        // Cancelled outside the lock, because cancelling runs the stopped run's callbacks on this thread.
        older?.Cancel();
        return run.WaitAsync(wait);
    }

    /// <summary>Stops every run of the baseline, or of the current solution and its views, for a baseline or configuration that changed.</summary>
    public void StopAll(bool baseline)
    {
        var stopped = new List<CancellationTokenSource>();
        lock (_lock)
        {
            foreach (var key in _latest.Keys.Where(k => k.Baseline == baseline).ToList())
            {
                stopped.Add(_latest[key].Stop);
                _latest.Remove(key);
            }
        }

        foreach (var stop in stopped)
            stop.Cancel();
    }

    /// <summary>
    ///     The token that stops the runs of <paramref name="analyzers"/>. A newer compilation of the project on that side
    ///     replaces the older one, whose runs <paramref name="older"/> is then to stop.
    /// </summary>
    private CancellationToken StopFor(ProjectId project, bool baseline, CompilationWithAnalyzers analyzers, out CancellationTokenSource? older)
    {
        older = null;
        if (_latest.TryGetValue((project, baseline), out var latest))
        {
            if (ReferenceEquals(latest.Analyzers, analyzers))
                return latest.Stop.Token;
            older = latest.Stop;
        }

        var stop = new CancellationTokenSource();
        _latest[(project, baseline)] = (analyzers, stop);
        return stop.Token;
    }

    /// <summary>The two runs a compilation can have.</summary>
    private sealed class Runs
    {
        public Task<ImmutableArray<Diagnostic>>? CompilationWide { get; set; }

        public Task<ImmutableArray<Diagnostic>>? Every { get; set; }
    }
}
