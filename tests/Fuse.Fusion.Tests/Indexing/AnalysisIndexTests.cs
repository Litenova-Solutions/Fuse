using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Fusion.Indexing;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Fusion.Tests.Indexing;

public sealed class AnalysisIndexTests : IDisposable
{
    private static readonly CapabilityRegistry<IDependencyExtractor> Extractors =
        new([new CSharpDependencyExtractor()]);

    private static readonly CapabilityRegistry<ITypeNameLocator> TypeLocators =
        new([new CSharpTypeNameLocator()]);

    private readonly string _root;

    public AnalysisIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DiskAnalysisIndex_PersistsAcrossInstances()
    {
        var key = AnalysisHasher.Key("public class A { }", "tier");
        var analysis = new FileAnalysis(["B"], ["A"], ["A", "M"]);

        var writer = new DiskAnalysisIndex(_root);
        writer.Set(key, analysis);

        var reader = new DiskAnalysisIndex(_root);
        Assert.True(reader.TryGet(key, out var loaded));
        Assert.Equal(["B"], loaded!.ReferencedTypes);
        Assert.Equal(["A"], loaded.DeclaredTypes);
        Assert.Equal(["A", "M"], loaded.DeclaredSymbols);
        Assert.Equal(1, reader.Statistics.Hits);
    }

    [Fact]
    public void DiskAnalysisIndex_MissRecordsMiss()
    {
        var index = new DiskAnalysisIndex(_root);
        Assert.False(index.TryGet("deadbeef", out _));
        Assert.Equal(1, index.Statistics.Misses);
    }

    [Fact]
    public async Task GraphBuild_SecondRunHitsIndex()
    {
        File.WriteAllText(Path.Combine(_root, "A.cs"), "class A { void M(B b) { } }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "class B { }");
        var files = new[] { CreateFile("A.cs"), CreateFile("B.cs") };

        var index = new DiskAnalysisIndex(_root);
        var builder = new DependencyGraphBuilder();

        // Cold build: both files miss and are stored.
        await builder.BuildAsync(files, new SourceContentProvider(new PhysicalFileSystem()),
            Extractors, TypeLocators, parallelism: 1, cancellationToken: default, index: index);
        var coldMisses = index.Statistics.Misses;

        // Warm build: both files are served from the index.
        var warm = new DiskAnalysisIndex(_root);
        var graph = await builder.BuildAsync(files, new SourceContentProvider(new PhysicalFileSystem()),
            Extractors, TypeLocators, parallelism: 1, cancellationToken: default, index: warm);

        Assert.Equal(2, coldMisses);
        Assert.Equal(2, warm.Statistics.Hits);
        Assert.Equal(0, warm.Statistics.Misses);
        // The warm graph is identical to a cold graph.
        Assert.Contains("B", graph.FileReferences["A.cs"]);
    }

    private SourceFile CreateFile(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath);
        return new SourceFile(new FileCandidate(fullPath, relativePath, new FileInfo(fullPath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
