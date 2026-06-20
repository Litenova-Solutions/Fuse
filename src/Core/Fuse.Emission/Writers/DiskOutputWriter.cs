using System.Text;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Emission.Writers;

/// <summary>
///     Writes fused content to disk with token tracking, automatic splitting, and filename generation.
/// </summary>
/// <remarks>
///     <para>
///         Disk side effects: content is streamed to a temporary file per part and renamed to its final
///         token-tagged name on rotation or completion via <see cref="OutputNamingService.FinalizeFile" />.
///         A part that received no tokens is deleted rather than finalized. Entries are written in the order
///         supplied by the caller, which the <see cref="EmissionPipeline" /> orders by descending token count.
///     </para>
///     <para>
///         Supports multi-part output: when <see cref="RotatePartAsync" /> is called the current part is
///         closed and finalized and a fresh temporary file is opened for subsequent entries. If the writer is
///         disposed before <see cref="CompleteAsync" />, the pending temporary file is deleted.
///     </para>
/// </remarks>
public sealed class DiskOutputWriter : IOutputWriter, IAsyncDisposable
{
    private const int MarkerOverheadTokens = 30;

    private readonly EmissionOptions _options;
    private readonly OutputNamingService _namingService;
    private readonly IEntryFormatter _entryFormatter;
    private readonly string _baseFileName;
    private readonly List<string> _createdFilePaths = new();
    private readonly List<FileTokenInfo>? _fileTokenStats;
    private readonly DateTime _startTime;

    private string _tempFilePath;
    private FileStream? _currentStream;
    private StreamWriter? _currentWriter;
    private int _currentPart = 1;
    private long _currentPartTokens;
    private bool _hasSplitOccurred;
    private int _processedFileCount;
    private bool _completed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiskOutputWriter" /> class.
    /// </summary>
    /// <param name="options">The emission options controlling output generation.</param>
    /// <param name="tokenCounter">The token counter for validation and tracking.</param>
    /// <param name="entryFormatter">The entry formatter for output blocks.</param>
    public DiskOutputWriter(EmissionOptions options, ITokenCounter tokenCounter, IEntryFormatter entryFormatter)
    {
        _options = options;
        _ = tokenCounter;
        _entryFormatter = entryFormatter;
        _namingService = new OutputNamingService();
        _baseFileName = _namingService.GetBaseFileName(options);
        _fileTokenStats = options.TrackTopTokenFiles || options.IncludeManifest
            ? new List<FileTokenInfo>()
            : null;
        _startTime = DateTime.Now;
        _tempFilePath = CreateTempFilePath(options.OutputDirectory);

        Directory.CreateDirectory(options.OutputDirectory);
        OpenStream(_tempFilePath);
    }

    /// <inheritdoc />
    public bool SupportsMultiPart => true;

    /// <inheritdoc />
    public async Task WritePrefixAsync(string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed || string.IsNullOrEmpty(content))
            return;

        await _currentWriter!.WriteAsync(content);
        await _currentWriter.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
        {
            throw new InvalidOperationException("Cannot write entries after emission has completed.");
        }

        if (content.IsTrivial)
        {
            return;
        }

        _fileTokenStats?.Add(new FileTokenInfo(content.NormalizedPath, content.TokenCount));

        var entryText = _entryFormatter.FormatEntry(content, _options);
        await _currentWriter!.WriteAsync(entryText);
        await _currentWriter.FlushAsync(cancellationToken);

        _currentPartTokens += content.TokenCount + MarkerOverheadTokens;
        _processedFileCount++;
    }

    /// <inheritdoc />
    public async Task RotatePartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
        {
            throw new InvalidOperationException("Cannot rotate parts after emission has completed.");
        }

        if (_currentPartTokens == 0)
        {
            return;
        }

        await CloseCurrentStreamAsync();

        var finalPath = _namingService.FinalizeFile(
            _tempFilePath,
            _options.OutputDirectory,
            _baseFileName,
            _currentPart,
            _currentPartTokens,
            _options.Overwrite,
            true);

        _createdFilePaths.Add(finalPath);

        _currentPart++;
        _currentPartTokens = 0;
        _hasSplitOccurred = true;

        _tempFilePath = CreateTempFilePath(_options.OutputDirectory);
        OpenStream(_tempFilePath);
    }

    /// <inheritdoc />
    public async Task<FusionResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_completed)
        {
            throw new InvalidOperationException("Emission has already completed.");
        }

        _completed = true;

        await CloseCurrentStreamAsync();

        if (_currentPartTokens > 0)
        {
            var finalPath = _namingService.FinalizeFile(
                _tempFilePath,
                _options.OutputDirectory,
                _baseFileName,
                _currentPart,
                _currentPartTokens,
                _options.Overwrite,
                _hasSplitOccurred);

            _createdFilePaths.Add(finalPath);
        }
        else if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }

        var duration = DateTime.Now - _startTime;
        var topTokenFiles = OutputWriterHelpers.BuildTopTokenFiles(_fileTokenStats);

        return new FusionResult(
            _createdFilePaths,
            null,
            0,
            _processedFileCount,
            0,
            duration,
            topTokenFiles,
            emittedFileTokens: _options.IncludeManifest ? _fileTokenStats : null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await CloseCurrentStreamAsync();

            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }
    }

    private void OpenStream(string path)
    {
        _currentStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        _currentWriter = new StreamWriter(_currentStream, Encoding.UTF8);
    }

    private async Task CloseCurrentStreamAsync()
    {
        if (_currentWriter is not null)
        {
            await _currentWriter.DisposeAsync();
            _currentWriter = null;
        }

        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync();
            _currentStream = null;
        }
    }

    private static string CreateTempFilePath(string outputDirectory)
    {
        var tempFileName = Path.GetRandomFileName();
        return Path.Combine(outputDirectory, tempFileName);
    }
}
