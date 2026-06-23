using System.Collections.Concurrent;
using System.Text;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Indexing;

/// <summary>
///     SQLite-backed <see cref="IAnalysisIndex" /> using the <c>analysis</c> store namespace, fronted by an
///     in-memory map so repeated lookups within a process do not re-read the database.
/// </summary>
/// <remarks>
///     The on-disk format is three tab-separated lines (referenced types, declared types, declared symbols).
///     Symbol names contain neither tabs nor newlines, so the format needs no escaping. Malformed entries are
///     treated as misses rather than throwing.
/// </remarks>
public sealed class SqliteAnalysisIndex : IAnalysisIndex
{
    private const string StoreName = "analysis";

    private static readonly string[] Empty = [];

    private readonly IKeyValueStore _store;
    private readonly ConcurrentDictionary<string, FileAnalysis> _memory = new(StringComparer.Ordinal);

    /// <summary>
    ///     Initializes a new instance of the <see cref="SqliteAnalysisIndex" /> class.
    /// </summary>
    /// <param name="store">The shared key-value store for the run.</param>
    public SqliteAnalysisIndex(IKeyValueStore store) => _store = store;

    /// <inheritdoc />
    public AnalysisIndexStatistics Statistics { get; } = new();

    /// <inheritdoc />
    public bool TryGet(string key, out FileAnalysis? analysis)
    {
        if (_memory.TryGetValue(key, out var cached))
        {
            Statistics.RecordHit();
            analysis = cached;
            return true;
        }

        if (_store.TryGet(StoreName, key, out var bytes) && bytes is not null)
        {
            try
            {
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                analysis = new FileAnalysis(Split(lines, 0), Split(lines, 1), Split(lines, 2));
                _memory[key] = analysis;
                Statistics.RecordHit();
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        Statistics.RecordMiss();
        analysis = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, FileAnalysis analysis)
    {
        _memory[key] = analysis;
        var content = string.Join('\t', analysis.ReferencedTypes) + "\n"
            + string.Join('\t', analysis.DeclaredTypes) + "\n"
            + string.Join('\t', analysis.DeclaredSymbols);
        _store.Set(StoreName, key, Encoding.UTF8.GetBytes(content));
    }

    private static IReadOnlyList<string> Split(string[] lines, int index)
    {
        if (index >= lines.Length || lines[index].Length == 0)
            return Empty;

        return lines[index].Split('\t', StringSplitOptions.RemoveEmptyEntries);
    }
}
