using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Tests.Dependencies;

public sealed class CountingSourceContentProvider : ISourceContentProvider
{
    private readonly IFileSystem _fileSystem;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public int ReadCount { get; private set; }

    public CountingSourceContentProvider(IFileSystem fileSystem) => _fileSystem = fileSystem;

    public async Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(file.FullPath, out var cached))
            return cached;

        ReadCount++;
        var content = await _fileSystem.ReadAllTextAsync(file.FullPath, cancellationToken);
        _cache[file.FullPath] = content;
        return content;
    }

    public void Clear()
    {
        _cache.Clear();
        ReadCount = 0;
    }
}
