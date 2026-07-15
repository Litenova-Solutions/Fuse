using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Fusion.Indexing;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Fusion.Tests.Indexing;

/// <summary>
///     Verifies that malformed bytes in the <c>analysis</c> store namespace are treated as cache misses and rebuilt.
/// </summary>
public sealed class AnalysisIndexMalformedEntryTests : IDisposable
{
    private static readonly CapabilityRegistry<IDependencyExtractor> Extractors =
        new([new RoslynDependencyExtractor()]);

    private static readonly CapabilityRegistry<ITypeNameLocator> TypeLocators =
        new([new RoslynTypeNameLocator()]);

    private static readonly string AnalysisTier =
        typeof(RoslynDependencyExtractor).FullName + "|" + typeof(RoslynTypeNameLocator).FullName;

    private readonly string _root;
    private readonly string _databasePath;

    public AnalysisIndexMalformedEntryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-malformed-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = SqliteTestHelpers.NewDatabasePath("fuse-malformed-analysis-tests");
    }

    [Fact]
    public async Task GraphBuild_MalformedAnalysisEntry_MissesAndReindexes()
    {
        const string source = "class Alpha { void M(Beta b) { } }";
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), source);
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "class Beta { }");
        var files = new[] { CreateFile("Alpha.cs"), CreateFile("Beta.cs") };
        var contentProvider = new SourceContentProvider(new PhysicalFileSystem());
        var builder = new DependencyGraphBuilder();
        var analysisKey = AnalysisHasher.Key(source, AnalysisTier);

        await using (var bootstrap = new SqliteKeyValueStore(_databasePath))
        {
            await bootstrap.FlushAsync();
        }

        SqliteTestHelpers.InsertStoreEntry(_databasePath, "analysis", analysisKey, [0xDE, 0xAD, 0xBE, 0xEF]);

        await using (var store = new SqliteKeyValueStore(_databasePath))
        {
            var coldIndex = new SqliteAnalysisIndex(store);
            var graph = await builder.BuildAsync(
                files,
                contentProvider,
                Extractors,
                TypeLocators,
                parallelism: 1,
                cancellationToken: default,
                index: coldIndex);

            Assert.Equal(2, coldIndex.Statistics.Misses);
            Assert.Equal(0, coldIndex.Statistics.Hits);
            Assert.Contains("Beta", graph.FileReferences["Alpha.cs"]);
            await store.FlushAsync();
        }

        await using (var warmStore = new SqliteKeyValueStore(_databasePath))
        {
            var warmIndex = new SqliteAnalysisIndex(warmStore);
            await builder.BuildAsync(
                files,
                contentProvider,
                Extractors,
                TypeLocators,
                parallelism: 1,
                cancellationToken: default,
                index: warmIndex);

            Assert.Equal(2, warmIndex.Statistics.Hits);
            Assert.Equal(0, warmIndex.Statistics.Misses);
        }

        Assert.False(SqliteTestHelpers.StoreEntryEqualsBytes(_databasePath, "analysis", analysisKey, [0xDE, 0xAD, 0xBE, 0xEF]));

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


        var databaseRoot = Path.GetDirectoryName(_databasePath);
        if (databaseRoot is not null && Directory.Exists(databaseRoot))
            Directory.Delete(databaseRoot, recursive: true);
    }
}
