using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Indexing;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorPersistenceTests : IDisposable
{
    private static readonly CapabilityRegistry<IDependencyExtractor> Extractors =
        new([new RoslynDependencyExtractor()]);

    private static readonly CapabilityRegistry<ITypeNameLocator> TypeLocators =
        new([new RoslynTypeNameLocator()]);

    private readonly string _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-persistence-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorPersistenceTests()
    {
        Directory.CreateDirectory(_sourceDirectory);
        SqliteTestHelpers.InitializeGitRepository(_sourceDirectory);
        File.WriteAllText(
            Path.Combine(_sourceDirectory, "Alpha.cs"),
            """
            public class Alpha
            {
                public string Name => "alpha-token";
            }
            """);
        File.WriteAllText(
            Path.Combine(_sourceDirectory, "Beta.cs"),
            """
            public class Beta
            {
                public int Value => 42;
            }
            """);

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_WithPersistentIndex_SecondRunReusesWarmAnalysisEntries()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = BuildQueryRequest(_sourceDirectory, "Alpha", usePersistentIndex: true);

        var first = await orchestrator.FuseAsync(request);
        var databasePath = SqliteTestHelpers.FuseDatabasePath(_sourceDirectory);
        Assert.True(File.Exists(databasePath));

        var analysisEntriesAfterFirst = SqliteTestHelpers.CountStoreEntries(databasePath, "analysis");
        Assert.True(analysisEntriesAfterFirst > 0);

        var second = await orchestrator.FuseAsync(request);
        Assert.Equal(first.InMemoryContent, second.InMemoryContent);

        // Unchanged sources should not grow the persisted analysis set on the warm run.
        Assert.Equal(analysisEntriesAfterFirst, SqliteTestHelpers.CountStoreEntries(databasePath, "analysis"));

        // A fresh index instance over the same database serves every file from disk.
        var files = new[]
        {
            CreateFile("Alpha.cs"),
            CreateFile("Beta.cs"),
        };
        await using var store = new SqliteKeyValueStore(databasePath);
        var warmIndex = new SqliteAnalysisIndex(store);
        var builder = _serviceProvider.GetRequiredService<DependencyGraphBuilder>();
        await builder.BuildAsync(
            files,
            new SourceContentProvider(new PhysicalFileSystem()),
            Extractors,
            TypeLocators,
            parallelism: 1,
            cancellationToken: default,
            index: warmIndex);

        Assert.Equal(files.Length, warmIndex.Statistics.Hits);
        Assert.Equal(0, warmIndex.Statistics.Misses);
    }

    [Fact]
    public async Task FuseAsync_WithCorruptDatabase_RecreatesAndSucceeds()
    {
        var fuseDirectory = Path.Combine(_sourceDirectory, ".fuse");
        Directory.CreateDirectory(fuseDirectory);
        File.WriteAllText(Path.Combine(fuseDirectory, "fuse.db"), "not a sqlite database");

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = BuildQueryRequest(_sourceDirectory, "Alpha", usePersistentIndex: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("Alpha.cs", result.InMemoryContent);
        Assert.True(File.Exists(SqliteTestHelpers.FuseDatabasePath(_sourceDirectory)));
    }

    private static FusionRequest BuildQueryRequest(string dir, string query, bool usePersistentIndex) =>
        new(
            new CollectionOptions(dir, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            query: new QueryOptions(query),
            useReductionCache: false,
            usePersistentIndex: usePersistentIndex);

    private SourceFile CreateFile(string relativePath)
    {
        var fullPath = Path.Combine(_sourceDirectory, relativePath);
        return new SourceFile(new FileCandidate(fullPath, relativePath, new FileInfo(fullPath)));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (!Directory.Exists(_sourceDirectory))
            return;

        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(_sourceDirectory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }
}
