using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Fuse.Check;

/// <summary>
///     What one check holds for itself, apart from the generation it pins: how it runs analyzers, whether it reuses
///     working-tree errors, and everything it counts while it binds. A check creates one context and every step of
///     that check reads and writes only it, so checks that run at the same time never overwrite or steal each
///     other's settings, timings or reports. The caches a context does not hold are shared between checks and are
///     keyed by what they depend on.
/// </summary>
internal sealed class CheckContext
{
    private readonly State _state;

    /// <param name="run">How this check runs analyzers, on the working tree and the baseline alike.</param>
    /// <param name="reuse">
    ///     True when this check reuses working-tree errors from an earlier check. The key needs Roslyn's dependent
    ///     semantic version, which costs more than it saves in a check of the files an edit just wrote, so a check
    ///     of named files neither reads nor fills the cache.
    /// </param>
    public CheckContext(AnalyzerRun run, bool reuse)
        : this(new State(), run, reuse)
    {
    }

    private CheckContext(State state, AnalyzerRun run, bool reuse)
    {
        _state = state;
        Run = run;
        Reuse = reuse;
    }

    /// <summary>How this check runs analyzers.</summary>
    public AnalyzerRun Run { get; }

    /// <summary>Whether this check reuses and keeps working-tree errors.</summary>
    public bool Reuse { get; }

    /// <summary>
    ///     This check with working-tree reuse on, sharing its counts, its reports and its caches with this one, so
    ///     the candidates a declaration change reaches reuse the errors an earlier check kept for them even in a
    ///     check of named files, whose targets reuse nothing. The targets keep this context as it is.
    /// </summary>
    public CheckContext WithReuse() => Reuse ? this : new CheckContext(_state, Run, reuse: true);

    /// <summary>
    ///     The declaration hash of <paramref name="solution"/> at <paramref name="configurationGeneration"/> for this
    ///     check, computed by <paramref name="compute"/> once however many files of the check ask for it, so the
    ///     per-file reuse key of every candidate reads the hash instead of hashing every project's declarations
    ///     again. A later check computes its own, from the solution it pins.
    /// </summary>
    /// <returns>The hash, or null when a project's declarations cannot be read, which the caller treats as no reuse.</returns>
    public Task<int?> DeclarationsHashAsync(Solution solution, int configurationGeneration, Func<Task<int?>> compute, CancellationToken cancellationToken) =>
        _state.Declarations.GetOrAdd(
            new DeclarationsKey(solution, configurationGeneration),
            _ => new Lazy<Task<int?>>(compute, LazyThreadSafetyMode.ExecutionAndPublication)).Value.WaitAsync(cancellationToken);

    /// <summary>Counts a file, by its relative path, or a project, by its name, among those whose analyzer errors this check may be missing.</summary>
    public void MarkUnfinished(string fileOrProject) => _state.Unfinished.TryAdd(fileOrProject, 0);

    /// <summary>Names a compilation-wide analyzer this check left out.</summary>
    public void MarkLeftOut(string analyzer) => _state.LeftOut.TryAdd(analyzer, 0);

    /// <summary>
    ///     What this check's analyzers did not do: how many files or projects their analyzers did not finish before
    ///     the deadline, and the compilation-wide analyzers the check left out. It takes the report, so a second
    ///     call for the same check answers empty.
    /// </summary>
    public (int Unfinished, IReadOnlyList<string> LeftOut) TakeAnalyzerReport()
    {
        var unfinished = _state.Unfinished.Count;
        _state.Unfinished.Clear();
        var leftOut = _state.LeftOut.Keys.Order(StringComparer.Ordinal).ToArray();
        _state.LeftOut.Clear();
        return (unfinished, leftOut);
    }

    /// <summary>How many working-tree files had their errors reused since the last call, for the engine log.</summary>
    public int TakeReused() => Interlocked.Exchange(ref _state.Reused, 0);

    /// <summary>Counts one working-tree file whose errors this check reused.</summary>
    public void CountReused() => Interlocked.Increment(ref _state.Reused);

    /// <summary>Counts one stored baseline result this check looked for.</summary>
    public void CountStoreLookup() => Interlocked.Increment(ref _state.StoreLookups);

    /// <summary>Counts one stored baseline result this check read.</summary>
    public void CountStoreHit() => Interlocked.Increment(ref _state.StoreHits);

    /// <summary>How many stored baseline results this check read, and how many it computed instead, for the engine log. It takes the counts.</summary>
    public (int Stored, int Computed) TakeBaselineReport()
    {
        var lookups = Interlocked.Exchange(ref _state.StoreLookups, 0);
        var hits = Interlocked.Exchange(ref _state.StoreHits, 0);
        return (hits, lookups - hits);
    }

    /// <summary>Counts one document this check bound.</summary>
    public void CountDocumentBound() => Interlocked.Increment(ref _state.DocumentsBound);

    /// <summary>Adds time this check spent updating a compilation.</summary>
    public void AddUpdateTicks(long ticks) => Interlocked.Add(ref _state.UpdateTicks, ticks);

    /// <summary>Adds time this check spent binding.</summary>
    public void AddCompilerTicks(long ticks) => Interlocked.Add(ref _state.CompilerTicks, ticks);

    /// <summary>Adds time this check spent running analyzers.</summary>
    public void AddAnalyzerTicks(long ticks) => Interlocked.Add(ref _state.AnalyzerTicks, ticks);

    /// <summary>Time spent binding and running analyzers since the last call, for the engine log. It takes the times.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings()
    {
        var compiler = Interlocked.Exchange(ref _state.CompilerTicks, 0);
        var analyzer = Interlocked.Exchange(ref _state.AnalyzerTicks, 0);
        return (compiler * 1000 / System.Diagnostics.Stopwatch.Frequency, analyzer * 1000 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Time spent updating the compilation, binding and running analyzers since the last call, and how many documents were bound, for the engine log. It takes the values.</summary>
    public (long UpdateMs, long CompilerMs, long AnalyzerMs, int Documents) TakeBindParts()
    {
        var update = Interlocked.Exchange(ref _state.UpdateTicks, 0);
        var (compilerMs, analyzerMs) = TakeTimings();
        var documents = Interlocked.Exchange(ref _state.DocumentsBound, 0);
        return (update * 1000 / System.Diagnostics.Stopwatch.Frequency, compilerMs, analyzerMs, documents);
    }

    /// <summary>What contexts of one check share: its counts, its analyzer reports and the declaration hashes it computed.</summary>
    private sealed class State
    {
        public long CompilerTicks;
        public long AnalyzerTicks;
        public long UpdateTicks;
        public int DocumentsBound;
        public int Reused;
        public int StoreLookups;
        public int StoreHits;
        public readonly ConcurrentDictionary<string, byte> Unfinished = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, byte> LeftOut = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<DeclarationsKey, Lazy<Task<int?>>> Declarations = new();
    }

    /// <summary>One solution at one configuration generation, which a declaration hash is computed for.</summary>
    private readonly record struct DeclarationsKey(Solution Solution, int Configuration);
}
