using System.Diagnostics;
using Fuse.Collection.FileSystem;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Emission.Writers;

/// <summary>
///     Writes a single table-of-contents document to disk or memory.
/// </summary>
/// <remarks>
///     Used when <see cref="EmissionOptions.TableOfContents" /> is enabled. The full document is supplied as
///     the prefix; no per-file entries are written.
/// </remarks>
public sealed class TableOfContentsOutputWriter : IOutputWriter, IAsyncDisposable
{
    private readonly EmissionOptions _options;
    private readonly bool _inMemory;
    private readonly IFileSystem _fileSystem;
    private readonly OutputNamingService _namingService;
    private readonly Stopwatch _stopwatch;
    private readonly List<FileTokenInfo> _emittedFileTokens;

    private string? _document;
    private bool _completed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TableOfContentsOutputWriter" /> class.
    /// </summary>
    /// <param name="options">Emission options controlling output location and format.</param>
    /// <param name="inMemory">When <see langword="true" />, capture the document in memory instead of writing to disk.</param>
    /// <param name="fileSystem">File system used when writing to disk.</param>
    /// <param name="emittedFileTokens">Per-file token costs listed in the table of contents.</param>
    public TableOfContentsOutputWriter(
        EmissionOptions options,
        bool inMemory,
        IFileSystem fileSystem,
        IReadOnlyList<FileTokenInfo> emittedFileTokens)
    {
        _options = options;
        _inMemory = inMemory;
        _fileSystem = fileSystem;
        _namingService = new OutputNamingService();
        _emittedFileTokens = emittedFileTokens.ToList();
        _stopwatch = Stopwatch.StartNew();
    }

    /// <inheritdoc />
    public bool SupportsMultiPart => false;

    /// <inheritdoc />
    public Task WritePrefixAsync(string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
            return Task.CompletedTask;

        _document = content;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task RotatePartAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public async Task<FusionResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
            throw new InvalidOperationException("Emission has already completed.");

        _completed = true;
        var document = _document ?? string.Empty;
        var duration = _stopwatch.Elapsed;

        if (_inMemory)
        {
            return new FusionResult(
                [],
                document,
                0,
                _emittedFileTokens.Count,
                0,
                duration,
                [],
                emittedFileTokens: _emittedFileTokens);
        }

        var baseName = _namingService.GetBaseFileName(_options);
        var fileName = OutputNamingService.BuildPartFileName(baseName, 1, 0, isMultiPart: false);
        Directory.CreateDirectory(_options.OutputDirectory);
        var path = Path.Combine(_options.OutputDirectory, fileName);
        await _fileSystem.WriteAllTextAsync(path, document);
        return new FusionResult(
            [path],
            null,
            0,
            _emittedFileTokens.Count,
            0,
            duration,
            [],
            emittedFileTokens: _emittedFileTokens);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
