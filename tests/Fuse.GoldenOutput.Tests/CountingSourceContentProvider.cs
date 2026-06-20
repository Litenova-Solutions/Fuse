using System.Collections.Concurrent;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.GoldenOutput.Tests;

internal sealed class CountingSourceContentProvider : ISourceContentProvider
{
    private readonly IFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _readCount;

    public int ReadCount => _readCount;

    public CountingSourceContentProvider(IFileSystem fileSystem) => _fileSystem = fileSystem;

    public async Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(file.FullPath, out var cached))
            return cached;

        Interlocked.Increment(ref _readCount);
        var content = await _fileSystem.ReadAllTextAsync(file.FullPath, cancellationToken);
        _cache[file.FullPath] = content;
        return content;
    }

    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _readCount, 0);
    }
}
