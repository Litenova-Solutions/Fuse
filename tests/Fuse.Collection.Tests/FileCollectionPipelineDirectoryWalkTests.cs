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

    [Fact]
    public async Task DirectoryWalk_JunctionDirectoryPointingOutside_ExcludesOutsideFiles()
    {
        // A junction is a reparse point like a directory symlink but needs no elevation, so this exercises the
        // escape-the-root exclusion on Windows (including dev machines) without the symlink privilege the
        // symlink test requires. Windows-only.
        if (!OperatingSystem.IsWindows())
            return;

        File.WriteAllText(Path.Combine(_rootDirectory, "Inside.cs"), "inside");
        File.WriteAllText(Path.Combine(_outsideDirectory, "Outside.cs"), "outside");

        var linkDirectory = Path.Combine(_rootDirectory, "escape");
        if (!TryCreateJunction(linkDirectory, _outsideDirectory))
            return;

        var options = new CollectionOptions(_rootDirectory) { Recursive = true };

        var result = await _pipeline.CollectAsync(options);

        Assert.Single(result.Files);
        Assert.Equal("Inside.cs", result.Files[0].RelativePath);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process!.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Remove any reparse-point (junction or directory symlink) link first, without following it, so the
        // recursive delete does not choke on or recurse through it. Best-effort: a temp-dir cleanup failure must
        // not fail the test.
        var escape = Path.Combine(_rootDirectory, "escape");
        try
        {
            if (Directory.Exists(escape))
                Directory.Delete(escape);
        }
        catch
        {
            // Ignore: the link is removed below by the recursive delete, or leaks harmlessly into temp.
        }

        var baseDirectory = Directory.GetParent(_rootDirectory)!.FullName;
        try
        {
            if (Directory.Exists(baseDirectory))
                Directory.Delete(baseDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a temp fixture; a leak is acceptable, a thrown teardown is not.
        }
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
