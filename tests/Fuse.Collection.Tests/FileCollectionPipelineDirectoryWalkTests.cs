using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests;

public sealed class FileCollectionPipelineDirectoryWalkTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _outsideDirectory;
    private readonly FileCollectionPipeline _pipeline;

    public FileCollectionPipelineDirectoryWalkTests()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "fuse-collection-walk", Guid.NewGuid().ToString("N"));
        _rootDirectory = Path.Combine(baseDirectory, "root");
        _outsideDirectory = Path.Combine(baseDirectory, "outside");
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_outsideDirectory);

        var fileSystem = new PhysicalFileSystem();
        _pipeline = new FileCollectionPipeline(fileSystem, new GitIgnoreParser(fileSystem), []);
    }

    [Fact]
    public async Task DirectoryWalk_IncludesFilesUnderRoot()
    {
        File.WriteAllText(Path.Combine(_rootDirectory, "Inside.cs"), "inside");

        var options = new CollectionOptions(_rootDirectory) { Recursive = true };

        var result = await _pipeline.CollectAsync(options);

        Assert.Single(result.Files);
        Assert.Equal("Inside.cs", result.Files[0].RelativePath);
    }

    [Fact]
    public async Task DirectoryWalk_SymlinkDirectoryPointingOutside_ExcludesOutsideFiles()
    {
        if (!SymbolicLinkSupport.IsAvailable)
            return;

        File.WriteAllText(Path.Combine(_rootDirectory, "Inside.cs"), "inside");
        File.WriteAllText(Path.Combine(_outsideDirectory, "Outside.cs"), "outside");

        var linkDirectory = Path.Combine(_rootDirectory, "escape");
        Directory.CreateSymbolicLink(linkDirectory, _outsideDirectory);

        var options = new CollectionOptions(_rootDirectory) { Recursive = true };

        var result = await _pipeline.CollectAsync(options);

        Assert.Single(result.Files);
        Assert.Equal("Inside.cs", result.Files[0].RelativePath);
    }

    [Fact]
    public async Task DirectoryWalk_SymlinkFilePointingOutside_IsSkipped()
    {
        if (!SymbolicLinkSupport.IsAvailable)
            return;

        var insidePath = Path.Combine(_rootDirectory, "Inside.cs");
        var outsidePath = Path.Combine(_outsideDirectory, "Outside.cs");
        File.WriteAllText(insidePath, "inside");
        File.WriteAllText(outsidePath, "outside");

        var linkPath = Path.Combine(_rootDirectory, "OutsideLink.cs");
        File.CreateSymbolicLink(linkPath, outsidePath);

        var options = new CollectionOptions(_rootDirectory) { Recursive = true };

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

    private static class SymbolicLinkSupport
    {
        public static bool IsAvailable { get; }

        static SymbolicLinkSupport()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                IsAvailable = false;
                return;
            }

            var probeDirectory = Path.Combine(Path.GetTempPath(), "fuse-symlink-probe", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probeDirectory);
                var target = Path.Combine(probeDirectory, "target.txt");
                File.WriteAllText(target, "probe");
                var link = Path.Combine(probeDirectory, "link.txt");
                File.CreateSymbolicLink(link, target);
                IsAvailable = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                IsAvailable = false;
            }
            finally
            {
                if (Directory.Exists(probeDirectory))
                    Directory.Delete(probeDirectory, recursive: true);
            }
        }
    }
}
