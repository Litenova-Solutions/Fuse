using System.Collections.Concurrent;
using Fuse.Collection.Models;

namespace Fuse.Collection.FileSystem;

/// <summary>
///     Read-through content cache keyed by full file path for a single fusion run.
/// </summary>
public sealed class SourceContentProvider : ISourceContentProvider
{
    private readonly IFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Initializes a new instance of the <see cref="SourceContentProvider" /> class.
    /// </summary>
    public SourceContentProvider(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public async Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(file.FullPath, out var cached))
            return cached;

        var content = await _fileSystem.ReadAllTextAsync(file.FullPath, cancellationToken);
        _cache[file.FullPath] = content;
        return content;
    }

    /// <inheritdoc />
    public void Clear() => _cache.Clear();
}
