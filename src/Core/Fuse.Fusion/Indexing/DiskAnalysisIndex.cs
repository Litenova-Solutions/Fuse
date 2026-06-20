using System.Collections.Concurrent;

namespace Fuse.Fusion.Indexing;

/// <summary>
///     Disk-backed <see cref="IAnalysisIndex" /> storing one small text file per key under <c>.fuse/index</c>,
///     fronted by an in-memory map so repeated lookups within a process do not re-read disk.
/// </summary>
/// <remarks>
///     The on-disk format is three tab-separated lines (referenced types, declared types, declared symbols).
///     Symbol names contain neither tabs nor newlines, so the format needs no escaping. Reads and writes are
///     resilient: a malformed or unreadable entry is treated as a miss rather than throwing, so a corrupted
///     index degrades to recomputation.
/// </remarks>
public sealed class DiskAnalysisIndex : IAnalysisIndex
{
    /// <summary>The directory name, relative to the source root, where index entries are written.</summary>
    public const string IndexDirectoryName = ".fuse/index";

    private static readonly string[] Empty = [];

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, FileAnalysis> _memory = new(StringComparer.Ordinal);

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiskAnalysisIndex" /> class for a source directory.
    /// </summary>
    /// <param name="sourceDirectory">The source root under which <c>.fuse/index</c> is created.</param>
    public DiskAnalysisIndex(string sourceDirectory)
    {
        _directory = Path.Combine(
            Path.GetFullPath(sourceDirectory),
            IndexDirectoryName.Replace('/', Path.DirectorySeparatorChar));
    }

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

        var path = Path.Combine(_directory, key + ".idx");
        if (File.Exists(path))
        {
            try
            {
                var lines = File.ReadAllLines(path);
                analysis = new FileAnalysis(Split(lines, 0), Split(lines, 1), Split(lines, 2));
                _memory[key] = analysis;
                Statistics.RecordHit();
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
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

        try
        {
            Directory.CreateDirectory(_directory);
            var content = string.Join('\t', analysis.ReferencedTypes) + "\n"
                + string.Join('\t', analysis.DeclaredTypes) + "\n"
                + string.Join('\t', analysis.DeclaredSymbols);
            File.WriteAllText(Path.Combine(_directory, key + ".idx"), content);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<string> Split(string[] lines, int index)
    {
        if (index >= lines.Length || lines[index].Length == 0)
            return Empty;

        return lines[index].Split('\t', StringSplitOptions.RemoveEmptyEntries);
    }
}
