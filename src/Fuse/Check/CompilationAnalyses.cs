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
    /// <summary>The most declaration-only results kept per project and side; past it the result unused the longest goes.</summary>
    private const int MaxDeclarationOnlyPerProject = 8;

    private readonly ConditionalWeakTable<CompilationWithAnalyzers, Runs> _runs = new();
    private readonly Dictionary<(ProjectId Project, bool Baseline), (CompilationWithAnalyzers Analyzers, CancellationTokenSource Stop)> _latest = [];
    private readonly Dictionary<DeclarationOnlyKey, (Task<ImmutableArray<Diagnostic>> Run, CancellationTokenSource Stop)> _declarationOnly = new();
    private readonly Dictionary<(ProjectId Project, bool Baseline), List<DeclarationOnlyKey>> _declarationOnlyOrder = new();
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

    /// <summary>
    ///     Stops the runs of an older compilation of <paramref name="project"/> on that side, for a check that reached
    ///     <paramref name="analyzers"/> without starting a run of its own, such as one that leaves the compilation-wide
    ///     analyzers out. Without it, a run past an earlier check's deadline would go on analyzing code an edit replaced.
    /// </summary>
    public void StopOlder(ProjectId project, bool baseline, CompilationWithAnalyzers analyzers)
    {
        CancellationTokenSource? older = null;
        lock (_lock)
        {
            if (_latest.TryGetValue((project, baseline), out var latest) && !ReferenceEquals(latest.Analyzers, analyzers))
            {
                older = latest.Stop;
                _latest.Remove((project, baseline));
            }
        }

        older?.Cancel();
    }

    /// <summary>
    ///     The diagnostics of the run of <paramref name="analyze"/> over the declaration-only analyzers, started now when
    ///     no run is going or done for the same declarations. Unlike <see cref="RunAsync"/>, the run is keyed by what the
    ///     analyzers read, the project's declarations, rather than by the compilation, so compilations a body edit apart
    ///     share it. A run that was stopped or failed is started again, and a replaced compilation stops no declaration-only
    ///     run, whose result holds precisely across replaced compilations with the same declarations.
    /// </summary>
    /// <param name="project">The project the compilation is of.</param>
    /// <param name="baseline">True for a compilation of the baseline, false for one of the current solution or a view of it.</param>
    /// <param name="declarations">The project's dependent semantic version, which a change inside a method body leaves as it is.</param>
    /// <param name="configuration">The configuration generation, which moves when the analyzers or their severities may have changed.</param>
    /// <param name="analyzers">The declaration-only analyzers that run, which identifies the run with the rest of the key.</param>
    /// <param name="analyze">Runs the analyzers; called at most once per run, with the token that stops it.</param>
    /// <param name="wait">Stops the wait, not the run. Once it is cancelled, no new run starts.</param>
    public Task<ImmutableArray<Diagnostic>> RunDeclarationOnlyAsync(
        ProjectId project,
        bool baseline,
        VersionStamp declarations,
        int configuration,
        string analyzers,
        Func<CancellationToken, Task<ImmutableArray<Diagnostic>>> analyze,
        CancellationToken wait)
    {
        Task<ImmutableArray<Diagnostic>> run;
        lock (_lock)
        {
            var key = new DeclarationOnlyKey(project, baseline, declarations, configuration, analyzers);
            if (_declarationOnly.TryGetValue(key, out var known) && !known.Run.IsCanceled && !known.Run.IsFaulted)
            {
                run = known.Run;
            }
            else
            {
                // A check past its deadline would not wait for a new run, which would then analyze for nobody.
                if (wait.IsCancellationRequested)
                    return Task.FromCanceled<ImmutableArray<Diagnostic>>(wait);
                var stop = new CancellationTokenSource();
                // Task.Run, so the analyzers' synchronous initialization runs outside the lock.
                run = Task.Run(() => analyze(stop.Token), stop.Token);
                _declarationOnly[key] = (run, stop);
                var side = (project, baseline);
                if (!_declarationOnlyOrder.TryGetValue(side, out var order))
                    _declarationOnlyOrder[side] = order = [];
                order.Remove(key);
                order.Add(key);
                while (order.Count > MaxDeclarationOnlyPerProject)
                {
                    _declarationOnly.Remove(order[0]);
                    order.RemoveAt(0);
                }
            }
        }

        return run.WaitAsync(wait);
    }

    /// <summary>
    ///     Forgets the declaration-only result kept for the key, so the next check runs the analyzers again. A run going
    ///     under it is left going for the checks already waiting for it.
    /// </summary>
    public void ForgetDeclarationOnly(ProjectId project, bool baseline, VersionStamp declarations, int configuration, string analyzers)
    {
        lock (_lock)
        {
            var key = new DeclarationOnlyKey(project, baseline, declarations, configuration, analyzers);
            _declarationOnly.Remove(key);
            if (_declarationOnlyOrder.TryGetValue((project, baseline), out var order))
                order.Remove(key);
        }
    }

    /// <summary>Stops every declaration-only run and forgets its result, for a configuration that changed.</summary>
    public void ClearDeclarationOnly()
    {
        var stopped = new List<CancellationTokenSource>();
        lock (_lock)
        {
            foreach (var (_, kept) in _declarationOnly)
                stopped.Add(kept.Stop);
            _declarationOnly.Clear();
            _declarationOnlyOrder.Clear();
        }

        foreach (var stop in stopped)
            stop.Cancel();
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

    /// <summary>What a declaration-only result was found for: the project and side, the declarations, the configuration and the analyzers.</summary>
    private sealed record DeclarationOnlyKey(ProjectId Project, bool Baseline, VersionStamp Declarations, int Configuration, string Analyzers);

    /// <summary>The two runs a compilation can have.</summary>
    private sealed class Runs
    {
        public Task<ImmutableArray<Diagnostic>>? CompilationWide { get; set; }

        public Task<ImmutableArray<Diagnostic>>? Every { get; set; }
    }
}
