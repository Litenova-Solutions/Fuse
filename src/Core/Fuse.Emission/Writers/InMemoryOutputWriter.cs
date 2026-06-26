using System.Diagnostics;
using System.Text;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Emission.Writers;

/// <summary>
///     Captures fused content in memory instead of writing to disk.
/// </summary>
/// <remarks>
///     Used by MCP server mode to produce fusion results without disk I/O.
///     Respects <see cref="EmissionOptions.MaxTokens" /> but does not split output into multiple parts.
/// </remarks>
public sealed class InMemoryOutputWriter : IOutputWriter
{
    private readonly EmissionOptions _options;
    private readonly IEntryFormatter _entryFormatter;
    private readonly StringBuilder _contentBuilder = new();
    private readonly List<FileTokenInfo> _fileTokenStats = new();
    private readonly Stopwatch _stopwatch;

    private int _processedFileCount;
    private bool _completed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InMemoryOutputWriter" /> class.
    /// </summary>
    /// <param name="options">The emission options controlling output generation.</param>
    /// <param name="tokenCounter">The token counter for validation and tracking.</param>
    /// <param name="entryFormatter">The entry formatter for output blocks.</param>
    public InMemoryOutputWriter(EmissionOptions options, ITokenCounter tokenCounter, IEntryFormatter entryFormatter)
    {
        _options = options;
        _ = tokenCounter;
        _entryFormatter = entryFormatter;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <inheritdoc />
    public bool SupportsMultiPart => false;

    /// <inheritdoc />
    public Task WritePrefixAsync(string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed || string.IsNullOrEmpty(content))
            return Task.CompletedTask;

        _contentBuilder.Append(content);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
        {
            throw new InvalidOperationException("Cannot write entries after emission has completed.");
        }

        if (content.IsTrivial)
        {
            return Task.CompletedTask;
        }

        if (_options.TrackTopTokenFiles || _options.IncludeManifest)
        {
            _fileTokenStats.Add(new FileTokenInfo(content.NormalizedPath, content.TokenCount));
        }

        _contentBuilder.Append(_entryFormatter.FormatEntry(content, _options));

        _processedFileCount++;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RotatePartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FusionResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
        {
            throw new InvalidOperationException("Emission has already completed.");
        }

        _completed = true;

        var duration = _stopwatch.Elapsed;
        var topTokenFiles = OutputWriterHelpers.BuildTopTokenFiles(
            _options.TrackTopTokenFiles ? _fileTokenStats : null);

        return Task.FromResult(new FusionResult(
            Array.Empty<string>(),
            _contentBuilder.ToString(),
            0,
            _processedFileCount,
            0,
            duration,
            topTokenFiles,
            emittedFileTokens: _options.IncludeManifest ? _fileTokenStats : null));
    }
}
