using Fuse.Analysis.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Tests.Dependencies;

public class DependencyGraphBuilderTests
{
    [Fact]
    public async Task BuildAsync_TwoFiles_FirstReferencesSecond_BuildsEdge()
    {
        var fs = new InMemoryFileSystem(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("/root", "A.cs")] = "public class A { public B Dep { get; set; } }",
            [Path.Combine("/root", "B.cs")] = "public class B { }",
        });

        var files = new[]
        {
            CreateFile("/root", "A.cs"),
            CreateFile("/root", "B.cs"),
        };

        var builder = new DependencyGraphBuilder();
        var graph = await builder.BuildAsync(files, fs, new CSharpDependencyExtractor());

        Assert.True(graph.FileReferences.ContainsKey("A.cs"));
        Assert.Contains("B", graph.FileReferences["A.cs"]);
        Assert.Contains("B.cs", graph.TypeIndex["B"]);
    }

    [Fact]
    public async Task BuildAsync_NoReferences_BuildsIsolatedNodes()
    {
        var fs = new InMemoryFileSystem(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("/root", "A.cs")] = "public class A { }",
            [Path.Combine("/root", "B.cs")] = "public class B { }",
        });

        var files = new[]
        {
            CreateFile("/root", "A.cs"),
            CreateFile("/root", "B.cs"),
        };

        var builder = new DependencyGraphBuilder();
        var graph = await builder.BuildAsync(files, fs, new CSharpDependencyExtractor());

        Assert.Empty(graph.FileReferences["A.cs"]);
        Assert.Empty(graph.FileReferences["B.cs"]);
    }

    [Fact]
    public async Task BuildAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var fs = new InMemoryFileSystem(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("/root", "A.cs")] = "public class A { }",
        });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var builder = new DependencyGraphBuilder();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildAsync([CreateFile("/root", "A.cs")], fs, new CSharpDependencyExtractor(), cts.Token));
    }

    private static SourceFile CreateFile(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath);
        var candidate = new FileCandidate(fullPath, relativePath, new FileInfo(fullPath));
        return new SourceFile(candidate);
    }

    private sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files;

        public InMemoryFileSystem(Dictionary<string, string> files) => _files = files;

        public bool DirectoryExists(string path) => true;
        public void CreateDirectory(string path) { }
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => _files.Keys;
        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files[path]);
        public FileInfo GetFileInfo(string path) => new(path);
        public bool IsBinaryFile(string filePath) => false;
        public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
    }
}
