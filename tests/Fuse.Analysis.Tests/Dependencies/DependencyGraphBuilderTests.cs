using Fuse.Languages.Abstractions;
using Fuse.Languages.Abstractions.Dependencies;
using Fuse.Languages.CSharp.Dependencies;
using Fuse.Analysis.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Tests.Dependencies;

public class DependencyGraphBuilderTests
{
    private static readonly CapabilityRegistry<IDependencyExtractor> Extractors =
        new([new CSharpDependencyExtractor()]);

    private static readonly CapabilityRegistry<ITypeNameLocator> TypeLocators =
        new([new CSharpTypeNameLocator()]);

    [Fact]
    public async Task BuildAsync_TwoFiles_FirstReferencesSecond_BuildsEdge()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine("/root", "A.cs"), "class A { void M(B b) { } }");
        fs.AddFile(Path.Combine("/root", "B.cs"), "class B { }");

        var files = new[] { CreateFile("/root", "A.cs"), CreateFile("/root", "B.cs") };
        var contentProvider = new SourceContentProvider(fs);
        var builder = new DependencyGraphBuilder();
        var graph = await builder.BuildAsync(files, contentProvider, Extractors, TypeLocators);

        Assert.True(graph.FileReferences.ContainsKey("A.cs"));
        Assert.Contains("B", graph.FileReferences["A.cs"]);
        Assert.Contains("B.cs", graph.TypeIndex["B"]);
    }

    [Fact]
    public async Task BuildAsync_NonCsFile_SkipsWithoutReading()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/root/readme.md", "# hello");

        var files = new[] { CreateFile("/root", "readme.md") };
        var contentProvider = new SourceContentProvider(fs);
        var builder = new DependencyGraphBuilder();
        var graph = await builder.BuildAsync(files, contentProvider, Extractors, TypeLocators);

        Assert.Empty(graph.FileReferences["readme.md"]);
    }

    [Fact]
    public async Task BuildAsync_CancellationRequested_Throws()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/root/A.cs", "class A { }");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var contentProvider = new SourceContentProvider(fs);
        var builder = new DependencyGraphBuilder();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildAsync([CreateFile("/root", "A.cs")], contentProvider, Extractors, TypeLocators, cts.Token));
    }

    [Fact]
    public async Task BuildAsync_ParallelAndSerial_ProduceSameGraph()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine("/root", "A.cs"), "class A { void M(B b) { } }");
        fs.AddFile(Path.Combine("/root", "B.cs"), "class B { public C Next { get; set; } }");
        fs.AddFile(Path.Combine("/root", "C.cs"), "class C { }");

        var files = new[]
        {
            CreateFile("/root", "A.cs"),
            CreateFile("/root", "B.cs"),
            CreateFile("/root", "C.cs"),
        };

        var builder = new DependencyGraphBuilder();
        var serialGraph = await builder.BuildAsync(
            files,
            new SourceContentProvider(fs),
            Extractors,
            TypeLocators,
            parallelism: 1);
        var parallelGraph = await builder.BuildAsync(
            files,
            new SourceContentProvider(fs),
            Extractors,
            TypeLocators,
            parallelism: 4);

        Assert.Equal(serialGraph.FileReferences.Keys.OrderBy(k => k), parallelGraph.FileReferences.Keys.OrderBy(k => k));
        foreach (var key in serialGraph.FileReferences.Keys)
        {
            Assert.Equal(
                serialGraph.FileReferences[key].OrderBy(t => t),
                parallelGraph.FileReferences[key].OrderBy(t => t));
        }
    }

    private static SourceFile CreateFile(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath);
        var candidate = new FileCandidate(fullPath, relativePath, new FileInfo(fullPath));
        return new SourceFile(candidate);
    }

    private sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path, string content) => _files[path] = content;

        public bool DirectoryExists(string path) => true;
        public void CreateDirectory(string path) { }
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => _files.Keys;
        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files[path]);
        public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            _files[path] = contents;
            return Task.CompletedTask;
        }
        public FileInfo GetFileInfo(string path) => new(path);
        public bool IsBinaryFile(string filePath) => false;
        public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
    }
}
