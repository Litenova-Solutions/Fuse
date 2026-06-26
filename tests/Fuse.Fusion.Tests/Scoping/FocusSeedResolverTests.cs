using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Fusion.Scoping;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Fusion.Tests.Scoping;

public class FocusSeedResolverTests
{
    [Fact]
    public async Task ExpandPaths_TransitiveDepthTwo_IncludesSecondHop()
    {
        var fs = new InMemoryFileSystem(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("/root", "A.cs")] = "public class A { public B Dep { get; set; } }",
            [Path.Combine("/root", "B.cs")] = "public class B { public C Next { get; set; } }",
            [Path.Combine("/root", "C.cs")] = "public class C { }",
        });

        var files = new[]
        {
            CreateFile("/root", "A.cs"),
            CreateFile("/root", "B.cs"),
            CreateFile("/root", "C.cs"),
        };

        var extractors = new CapabilityRegistry<IDependencyExtractor>([new RoslynDependencyExtractor()]);
        var typeLocators = new CapabilityRegistry<ITypeNameLocator>([new RoslynTypeNameLocator()]);

        var contentProvider = new SourceContentProvider(fs);
        var graph = await new DependencyGraphBuilder().BuildAsync(files, contentProvider, extractors, typeLocators);
        var resolver = new FocusSeedResolver(typeLocators);
        var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.cs" };

        var depthOne = resolver.ExpandPaths(graph, seeds, depth: 1);
        Assert.Contains("A.cs", depthOne.IncludedPaths);
        Assert.Contains("B.cs", depthOne.IncludedPaths);
        Assert.DoesNotContain("C.cs", depthOne.IncludedPaths);

        var depthTwo = resolver.ExpandPaths(graph, seeds, depth: 2);
        Assert.Contains("A.cs", depthTwo.IncludedPaths);
        Assert.Contains("B.cs", depthTwo.IncludedPaths);
        Assert.Contains("C.cs", depthTwo.IncludedPaths);
    }

    [Fact]
    public async Task ExpandPaths_RecordsProvenanceChains()
    {
        var fs = new InMemoryFileSystem(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("/root", "A.cs")] = "public class A { public B Dep { get; set; } }",
            [Path.Combine("/root", "B.cs")] = "public class B { public C Next { get; set; } }",
            [Path.Combine("/root", "C.cs")] = "public class C { }",
        });

        var files = new[]
        {
            CreateFile("/root", "A.cs"),
            CreateFile("/root", "B.cs"),
            CreateFile("/root", "C.cs"),
        };

        var extractors = new CapabilityRegistry<IDependencyExtractor>([new RoslynDependencyExtractor()]);
        var typeLocators = new CapabilityRegistry<ITypeNameLocator>([new RoslynTypeNameLocator()]);

        var contentProvider = new SourceContentProvider(fs);
        var graph = await new DependencyGraphBuilder().BuildAsync(files, contentProvider, extractors, typeLocators);
        var resolver = new FocusSeedResolver(typeLocators);
        var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.cs" };

        var expansion = resolver.ExpandPaths(graph, seeds, depth: 2);

        Assert.Equal(["A.cs", "B.cs"], expansion.ProvenanceChains["B.cs"]);
        Assert.Equal(["A.cs", "B.cs", "C.cs"], expansion.ProvenanceChains["C.cs"]);
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
