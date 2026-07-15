using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R35: one hostile file (oversized, unreadable) must never abort the whole index. The scan skips it with a
// recorded reason and keeps indexing the good files, which stay findable.
public sealed class WorkspaceFileScannerSkipTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-scan-skip", Guid.NewGuid().ToString("N"));

    public WorkspaceFileScannerSkipTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task OversizedFile_IsSkipped_GoodFilesStillIndexed()
    {
        WriteText("src/Good.cs", "namespace App; public class Good { }");
        // A text file larger than the default 5 MB cap: passes the binary filter, then our size check skips it.
        var big = Path.Combine(_root, "src", "Big.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        var content = new byte[WorkspaceFileScanner.DefaultMaxFileBytes + 1024];
        Array.Fill(content, (byte)'A'); // non-null bytes so it is not treated as binary.
        await File.WriteAllBytesAsync(big, content);

        var result = await CreateScanner().ScanWithSkipsAsync(new FileScanRequest(_root), CancellationToken.None);

        Assert.Contains(result.Files, f => Norm(f.NormalizedPath).EndsWith("src/Good.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, f => Norm(f.NormalizedPath).EndsWith("src/Big.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Skipped, s => Norm(s.Path).EndsWith("src/Big.cs", StringComparison.OrdinalIgnoreCase) && s.Reason.Contains("too large", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LockedFile_IsSkipped_IndexingContinues()
    {
        WriteText("src/Good.cs", "namespace App; public class Good { }");
        var locked = Path.Combine(_root, "src", "Locked.cs");
        WriteText("src/Locked.cs", "namespace App; public class Locked { }");

        // Hold the file with an exclusive lock during the scan. On Windows this makes the read throw; the scan
        // must skip it and still index the good file rather than aborting.
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await CreateScanner().ScanWithSkipsAsync(new FileScanRequest(_root), CancellationToken.None);

        // The core guarantee (all platforms): the good file is indexed and the scan never throws, even with a
        // locked file present. On Windows the exclusive lock also keeps the locked file out of the index (it is
        // dropped either by the binary-sniff filter's failed read or by our per-file skip); the recorded-skip path
        // itself is covered by the oversized-file test.
        Assert.Contains(result.Files, f => Norm(f.NormalizedPath).EndsWith("src/Good.cs", StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(result.Files, f => Norm(f.NormalizedPath).EndsWith("src/Locked.cs", StringComparison.OrdinalIgnoreCase));
    }

    private static string Norm(string path) => path.Replace('\\', '/');

    private static WorkspaceFileScanner CreateScanner()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new WorkspaceFileScanner(pipeline, new FileHashService());
    }

    private void WriteText(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
