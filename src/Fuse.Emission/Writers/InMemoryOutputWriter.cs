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
    private readonly StringBuilder _contentBuilder = new();
    private readonly List<FileTokenInfo> _fileTokenStats = new();
    private readonly DateTime _startTime;

    private int _processedFileCount;
    private bool _completed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InMemoryOutputWriter" /> class.
    /// </summary>
    /// <param name="options">The emission options controlling output generation.</param>
    /// <param name="tokenCounter">The token counter for validation and tracking.</param>
    public InMemoryOutputWriter(EmissionOptions options, ITokenCounter tokenCounter)
    {
        _options = options;
        _ = tokenCounter;
        _startTime = DateTime.Now;
    }

    /// <inheritdoc />
    public bool SupportsMultiPart => false;

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

        if (_options.TrackTopTokenFiles)
        {
            _fileTokenStats.Add(new FileTokenInfo(content.NormalizedPath, content.TokenCount));
        }

        if (_options.IncludeMetadata)
        {
            var fileInfo = content.SourceFile.FileInfo;
            _contentBuilder.AppendLine(
                $"<file path=\"{content.NormalizedPath}\" size=\"{fileInfo.Length}\" modified=\"{fileInfo.LastWriteTimeUtc:O}\">");
        }
        else
        {
            _contentBuilder.AppendLine($"<file path=\"{content.NormalizedPath}\">");
        }

        _contentBuilder.Append(content.Content);

        if (!content.Content.EndsWith('\n'))
        {
            _contentBuilder.AppendLine();
        }

        _contentBuilder.AppendLine("</file>");

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

        var duration = DateTime.Now - _startTime;
        var topTokenFiles = _options.TrackTopTokenFiles
            ? (IReadOnlyList<FileTokenInfo>)_fileTokenStats.OrderByDescending(f => f.Count).Take(5).ToList()
            : Array.Empty<FileTokenInfo>();

        return Task.FromResult(new FusionResult(
            Array.Empty<string>(),
            _contentBuilder.ToString(),
            0,
            _processedFileCount,
            0,
            duration,
            topTokenFiles));
    }
}
