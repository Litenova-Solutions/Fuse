using System.Collections.Concurrent;

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
    private long _compilerTicks;
    private long _analyzerTicks;
    private long _updateTicks;
    private int _documentsBound;
    private int _reused;
    private int _storeLookups;
    private int _storeHits;
    private readonly ConcurrentDictionary<string, byte> _unfinished = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _leftOut = new(StringComparer.Ordinal);

    /// <param name="run">How this check runs analyzers, on the working tree and the baseline alike.</param>
    /// <param name="reuse">
    ///     True when this check reuses working-tree errors from an earlier check. The key needs Roslyn's dependent
    ///     semantic version, which costs more than it saves in a check of the files an edit just wrote, so a check
    ///     of named files neither reads nor fills the cache.
    /// </param>
    public CheckContext(AnalyzerRun run, bool reuse)
    {
        Run = run;
        Reuse = reuse;
    }

    /// <summary>How this check runs analyzers.</summary>
    public AnalyzerRun Run { get; }

    /// <summary>Whether this check reuses and keeps working-tree errors.</summary>
    public bool Reuse { get; }

    /// <summary>Counts a file, by its relative path, or a project, by its name, among those whose analyzer errors this check may be missing.</summary>
    public void MarkUnfinished(string fileOrProject) => _unfinished.TryAdd(fileOrProject, 0);

    /// <summary>Names a compilation-wide analyzer this check left out.</summary>
    public void MarkLeftOut(string analyzer) => _leftOut.TryAdd(analyzer, 0);

    /// <summary>
    ///     What this check's analyzers did not do: how many files or projects their analyzers did not finish before
    ///     the deadline, and the compilation-wide analyzers the check left out. It takes the report, so a second
    ///     call for the same check answers empty.
    /// </summary>
    public (int Unfinished, IReadOnlyList<string> LeftOut) TakeAnalyzerReport()
    {
        var unfinished = _unfinished.Count;
        _unfinished.Clear();
        var leftOut = _leftOut.Keys.Order(StringComparer.Ordinal).ToArray();
        _leftOut.Clear();
        return (unfinished, leftOut);
    }

    /// <summary>How many working-tree files had their errors reused since the last call, for the engine log.</summary>
    public int TakeReused() => Interlocked.Exchange(ref _reused, 0);

    /// <summary>Counts one working-tree file whose errors this check reused.</summary>
    public void CountReused() => Interlocked.Increment(ref _reused);

    /// <summary>Counts one stored baseline result this check looked for.</summary>
    public void CountStoreLookup() => Interlocked.Increment(ref _storeLookups);

    /// <summary>Counts one stored baseline result this check read.</summary>
    public void CountStoreHit() => Interlocked.Increment(ref _storeHits);

    /// <summary>How many stored baseline results this check read, and how many it computed instead, for the engine log. It takes the counts.</summary>
    public (int Stored, int Computed) TakeBaselineReport()
    {
        var lookups = Interlocked.Exchange(ref _storeLookups, 0);
        var hits = Interlocked.Exchange(ref _storeHits, 0);
        return (hits, lookups - hits);
    }

    /// <summary>Counts one document this check bound.</summary>
    public void CountDocumentBound() => Interlocked.Increment(ref _documentsBound);

    /// <summary>Adds time this check spent updating a compilation.</summary>
    public void AddUpdateTicks(long ticks) => Interlocked.Add(ref _updateTicks, ticks);

    /// <summary>Adds time this check spent binding.</summary>
    public void AddCompilerTicks(long ticks) => Interlocked.Add(ref _compilerTicks, ticks);

    /// <summary>Adds time this check spent running analyzers.</summary>
    public void AddAnalyzerTicks(long ticks) => Interlocked.Add(ref _analyzerTicks, ticks);

    /// <summary>Time spent binding and running analyzers since the last call, for the engine log. It takes the times.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings()
    {
        var compiler = Interlocked.Exchange(ref _compilerTicks, 0);
        var analyzer = Interlocked.Exchange(ref _analyzerTicks, 0);
        return (compiler * 1000 / System.Diagnostics.Stopwatch.Frequency, analyzer * 1000 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Time spent updating the compilation, binding and running analyzers since the last call, and how many documents were bound, for the engine log. It takes the values.</summary>
    public (long UpdateMs, long CompilerMs, long AnalyzerMs, int Documents) TakeBindParts()
    {
        var update = Interlocked.Exchange(ref _updateTicks, 0);
        var (compilerMs, analyzerMs) = TakeTimings();
        var documents = Interlocked.Exchange(ref _documentsBound, 0);
        return (update * 1000 / System.Diagnostics.Stopwatch.Frequency, compilerMs, analyzerMs, documents);
    }
}
