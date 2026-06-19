using System.Text;
using Fuse.Emission.Models;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Emission.Writers;

/// <summary>
///     Writes fused content to disk with token tracking, automatic splitting, and filename generation.
/// </summary>
public sealed class DiskOutputWriter : IOutputWriter, IAsyncDisposable
{
    private const int MarkerOverheadTokens = 30;

    private readonly EmissionOptions _options;
    private readonly OutputNamingService _namingService;
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
    public DiskOutputWriter(EmissionOptions options, ITokenCounter tokenCounter)
    {
        _options = options;
        _ = tokenCounter;
        _namingService = new OutputNamingService();
        _baseFileName = _namingService.GetBaseFileName(options);
        _fileTokenStats = options.TrackTopTokenFiles ? new List<FileTokenInfo>() : null;
        _startTime = DateTime.Now;
        _tempFilePath = CreateTempFilePath(options.OutputDirectory);

        Directory.CreateDirectory(options.OutputDirectory);
        OpenStream(_tempFilePath);
    }

    /// <inheritdoc />
    public bool SupportsMultiPart => true;

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

        var entryText = FormatEntry(content);
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
        var topTokenFiles = BuildTopTokenFiles();

        return new FusionResult(
            _createdFilePaths,
            null,
            0,
            _processedFileCount,
            0,
            duration,
            topTokenFiles);
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

    private string FormatEntry(FusedContent content)
    {
        var sb = new StringBuilder();

        if (_options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            sb.AppendLine(
                $"<file path=\"{content.NormalizedPath}\" size=\"{fileInfo.Length}\" modified=\"{fileInfo.LastWriteTimeUtc:O}\">");
        }
        else
        {
            sb.AppendLine($"<file path=\"{content.NormalizedPath}\">");
        }

        sb.Append(content.Content);

        if (!content.Content.EndsWith('\n'))
        {
            sb.AppendLine();
        }

        sb.AppendLine("</file>");

        return sb.ToString();
    }

    private IReadOnlyList<FileTokenInfo> BuildTopTokenFiles()
    {
        if (_fileTokenStats is null)
        {
            return Array.Empty<FileTokenInfo>();
        }

        return _fileTokenStats
            .OrderByDescending(f => f.Count)
            .Take(5)
            .ToList();
    }
}
