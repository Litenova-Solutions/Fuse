using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     The analyzer runs over whole compilations, each one shared by every file and every check that needs it. A run
///     belongs to its compilation, not to the check that started it: when that check's deadline passes or its client stops
///     waiting, the run goes on, so the next check of the same compilation finds it done instead of starting again. A
///     newer compilation of the same project on the same side stops the older one's runs only when no pinned check
///     waits on them: a run a pinned check waits on is reference-counted by that wait, and a stop asked for while a
///     pin holds is taken when the last pin goes. The newest wrapper of a project is held weakly, so it keeps no
///     compilation after both views have moved on.
/// </summary>
internal sealed class CompilationAnalyses
{
    private readonly ConditionalWeakTable<CompilationWithAnalyzers, Runs> _runs = new();

    /// <summary>
    ///     The newest wrapper of each project on each side and the token that stops its runs, held weakly: the wrapper
    ///     roots its compilation, so a strong entry would keep that compilation after both views have moved on.
    /// </summary>
    private readonly Dictionary<(ProjectId Project, bool Baseline), (WeakReference<CompilationWithAnalyzers> Analyzers, CancellationTokenSource Stop)> _latest = [];
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
                var stop = StopFor(project, baseline, analyzers, runs, out older);
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

    /// <summary>
    ///     The diagnostics of a run a pinned check waits on. The wait counts as a pin on the compilation: a newer
    ///     compilation that would stop this run while the pin holds takes its stop when the pin goes instead, so it
    ///     never cancels work the pinned check is waiting for.
    /// </summary>
    /// <param name="project">The project the compilation is of.</param>
    /// <param name="baseline">True for a compilation of the baseline, false for one of the current solution or a view of it.</param>
    /// <param name="analyzers">The compilation with its analyzers, which identifies the run.</param>
    /// <param name="everyAnalyzer">True for the run of every analyzer with the compiler's diagnostics, false for the compilation-wide analyzers alone.</param>
    /// <param name="analyze">Runs the analyzers; called at most once per run, with the token that stops it.</param>
    /// <param name="context">The pinned check that waits; its presence is the pin.</param>
    /// <param name="wait">Stops the wait, not the run. Once it is cancelled, no new run starts.</param>
    public async Task<ImmutableArray<Diagnostic>> RunAsync(
        ProjectId project, bool baseline, CompilationWithAnalyzers analyzers, bool everyAnalyzer, Func<CancellationToken, Task<ImmutableArray<Diagnostic>>> analyze, CheckContext context, CancellationToken wait)
    {
        _ = context;
        var runs = Pin(analyzers);
        try
        {
            return await RunAsync(project, baseline, analyzers, everyAnalyzer, analyze, wait).ConfigureAwait(false);
        }
        finally
        {
            Unpin(runs);
        }
    }

    /// <summary>
    ///     Stops the runs of an older compilation of <paramref name="project"/> on that side, for a check that reached
    ///     <paramref name="analyzers"/> without starting a run of its own, such as one that leaves the compilation-wide
    ///     analyzers out. Without it, a run past an earlier check's deadline would go on analyzing code an edit replaced.
    ///     A run a pinned check waits on is stopped when its last pin goes instead.
    /// </summary>
    public void StopOlder(ProjectId project, bool baseline, CompilationWithAnalyzers analyzers)
    {
        CancellationTokenSource? older = null;
        lock (_lock)
        {
            if (_latest.TryGetValue((project, baseline), out var latest)
                && (!latest.Analyzers.TryGetTarget(out var current) || !ReferenceEquals(current, analyzers)))
            {
                older = ScheduleStopLocked(latest);
                _latest.Remove((project, baseline));
            }
        }

        older?.Cancel();
    }

    /// <summary>Stops every run of the baseline, or of the current solution and its views, for a baseline or configuration that changed. A run a pinned check waits on is stopped when its last pin goes instead.</summary>
    public void StopAll(bool baseline)
    {
        var stopped = new List<CancellationTokenSource>();
        lock (_lock)
        {
            foreach (var key in _latest.Keys.Where(k => k.Baseline == baseline).ToList())
            {
                if (ScheduleStopLocked(_latest[key]) is { } stop)
                    stopped.Add(stop);
                _latest.Remove(key);
            }
        }

        foreach (var stop in stopped)
            stop.Cancel();
    }

    /// <summary>Counts one pinned wait on <paramref name="analyzers"/> and returns the runs it pins.</summary>
    private Runs Pin(CompilationWithAnalyzers analyzers)
    {
        lock (_lock)
        {
            var runs = _runs.GetOrCreateValue(analyzers);
            runs.Pins++;
            return runs;
        }
    }

    /// <summary>Drops one pinned wait, and takes a stop that was asked for while pins held when the last one goes.</summary>
    private void Unpin(Runs runs)
    {
        CancellationTokenSource? stop = null;
        lock (_lock)
        {
            runs.Pins--;
            if (runs.Pins == 0 && runs.StopPending)
            {
                runs.StopPending = false;
                stop = runs.PendingStop;
            }
        }

        stop?.Cancel();
    }

    /// <summary>
    ///     The stop to cancel now for <paramref name="latest"/>, under the lock, or null when a pinned check waits on
    ///     its runs, whose stop is then pending until the last pin goes.
    /// </summary>
    private CancellationTokenSource? ScheduleStopLocked((WeakReference<CompilationWithAnalyzers> Analyzers, CancellationTokenSource Stop) latest)
    {
        if (latest.Analyzers.TryGetTarget(out var wrapper) && _runs.TryGetValue(wrapper, out var runs) && runs.Pins > 0)
        {
            runs.StopPending = true;
            runs.PendingStop = latest.Stop;
            return null;
        }

        return latest.Stop;
    }

    /// <summary>
    ///     The token that stops the runs of <paramref name="analyzers"/>. A newer compilation of the project on that side
    ///     replaces the older one, whose runs <paramref name="older"/> is then to stop, unless a pinned check waits on
    ///     them, in which case the stop is pending until its last pin goes.
    /// </summary>
    private CancellationToken StopFor(ProjectId project, bool baseline, CompilationWithAnalyzers analyzers, Runs runs, out CancellationTokenSource? older)
    {
        older = null;
        if (_latest.TryGetValue((project, baseline), out var latest))
        {
            if (latest.Analyzers.TryGetTarget(out var current) && ReferenceEquals(current, analyzers))
                return latest.Stop.Token;
            older = ScheduleStopLocked(latest);
        }

        var stop = new CancellationTokenSource();
        _latest[(project, baseline)] = (new WeakReference<CompilationWithAnalyzers>(analyzers), stop);
        return stop.Token;
    }

    /// <summary>The two runs a compilation can have, a stop asked for while pins held, and how many pinned checks wait on them.</summary>
    private sealed class Runs
    {
        public Task<ImmutableArray<Diagnostic>>? CompilationWide { get; set; }

        public Task<ImmutableArray<Diagnostic>>? Every { get; set; }

        public CancellationTokenSource? PendingStop { get; set; }

        public int Pins { get; set; }

        public bool StopPending { get; set; }
    }
}
