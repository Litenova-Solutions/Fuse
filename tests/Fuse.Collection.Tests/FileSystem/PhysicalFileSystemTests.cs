using Fuse.Collection.FileSystem;

namespace Fuse.Collection.Tests.FileSystem;

public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PhysicalFileSystem _fileSystem = new();

    public PhysicalFileSystemTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "fuse-pfs-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void IsBinaryFile_EmptyFile_ReturnsFalse()
    {
        var path = Path.Combine(_tempDirectory, "empty.txt");
        File.WriteAllText(path, string.Empty);

        Assert.False(_fileSystem.IsBinaryFile(path));
    }

    [Fact]
    public void IsBinaryFile_TextFile_ReturnsFalse()
    {
        var path = Path.Combine(_tempDirectory, "text.cs");
        File.WriteAllText(path, "public class Example { }");

        Assert.False(_fileSystem.IsBinaryFile(path));
    }

    [Fact]
    public void IsBinaryFile_FileWithNullByte_ReturnsTrue()
    {
        var path = Path.Combine(_tempDirectory, "binary.bin");
        File.WriteAllBytes(path, "hello\0world"u8.ToArray());

        Assert.True(_fileSystem.IsBinaryFile(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
