using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class BinaryFileFilterTests
{
    [Fact]
    public void Include_IgnoreBinaryDisabled_ReturnsTrue()
    {
        var fileSystem = new StubFileSystem();
        var filter = new BinaryFileFilter(fileSystem);
        var options = new CollectionOptions(@"C:\src", ignoreBinaryFiles: false);
        var candidate = TestHelpers.CreateCandidate("image.png");

        Assert.True(filter.Include(candidate, options));
    }

    [Fact]
    public void Include_TextFile_ReturnsTrue()
    {
        var fileSystem = new StubFileSystem();
        fileSystem.SetBinary(@"C:\src\Program.cs", isBinary: false);
        var filter = new BinaryFileFilter(fileSystem);
        var options = TestHelpers.DefaultOptions();
        var candidate = new FileCandidate(@"C:\src\Program.cs", "Program.cs", new FileInfo(@"C:\src\Program.cs"));

        Assert.True(filter.Include(candidate, options));
    }

    [Fact]
    public void Include_BinaryFile_ReturnsFalse()
    {
        var fileSystem = new StubFileSystem();
        fileSystem.SetBinary(@"C:\src\image.png", isBinary: true);
        var filter = new BinaryFileFilter(fileSystem);
        var options = TestHelpers.DefaultOptions();
        var candidate = new FileCandidate(@"C:\src\image.png", "image.png", new FileInfo(@"C:\src\image.png"));

        Assert.False(filter.Include(candidate, options));
    }

    private sealed class StubFileSystem : IFileSystem
    {
        private readonly Dictionary<string, bool> _binaryFiles = new(StringComparer.OrdinalIgnoreCase);

        public void SetBinary(string path, bool isBinary) => _binaryFiles[path] = isBinary;

        public bool DirectoryExists(string path) => true;

        public void CreateDirectory(string path) { }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) =>
            [];

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public FileInfo GetFileInfo(string path) => new(path);

        public bool IsBinaryFile(string filePath) =>
            _binaryFiles.TryGetValue(filePath, out var isBinary) && isBinary;

        public string GetRelativePath(string relativeTo, string path) => path;
    }
}
