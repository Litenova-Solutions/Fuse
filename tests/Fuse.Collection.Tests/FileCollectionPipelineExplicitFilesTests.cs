using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests;

public sealed class FileCollectionPipelineExplicitFilesTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _outsideDirectory;
    private readonly FileCollectionPipeline _pipeline;

    public FileCollectionPipelineExplicitFilesTests()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "fuse-collection-explicit", Guid.NewGuid().ToString("N"));
        _rootDirectory = Path.Combine(baseDirectory, "root");
        _outsideDirectory = Path.Combine(baseDirectory, "outside");
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_outsideDirectory);

        var fileSystem = new PhysicalFileSystem();
        _pipeline = new FileCollectionPipeline(fileSystem, new GitIgnoreParser(fileSystem), []);
    }

    [Fact]
    public async Task ExplicitFiles_ParentDirectoryEscape_IsSkipped()
    {
        var insidePath = Path.Combine(_rootDirectory, "Inside.cs");
        var outsidePath = Path.Combine(_outsideDirectory, "Outside.cs");
        File.WriteAllText(insidePath, "inside");
        File.WriteAllText(outsidePath, "outside");

        var options = new CollectionOptions(_rootDirectory)
        {
            ExplicitFiles = ["Inside.cs", Path.Combine("..", "outside", "Outside.cs")]
        };

        var result = await _pipeline.CollectAsync(options);

        Assert.Single(result.Files);
        Assert.Equal("Inside.cs", result.Files[0].RelativePath);
    }

    [Fact]
    public async Task ExplicitFiles_AbsolutePathOutsideRoot_IsSkipped()
    {
        var insidePath = Path.Combine(_rootDirectory, "Inside.cs");
        var outsidePath = Path.Combine(_outsideDirectory, "Outside.cs");
        File.WriteAllText(insidePath, "inside");
        File.WriteAllText(outsidePath, "outside");

        var options = new CollectionOptions(_rootDirectory)
        {
            ExplicitFiles = ["Inside.cs", Path.GetFullPath(outsidePath)]
        };

        var result = await _pipeline.CollectAsync(options);

        Assert.Single(result.Files);
        Assert.Equal("Inside.cs", result.Files[0].RelativePath);
    }

    public void Dispose()
    {
        var baseDirectory = Directory.GetParent(_rootDirectory)!.FullName;
        if (Directory.Exists(baseDirectory))
            Directory.Delete(baseDirectory, recursive: true);
    }
}
