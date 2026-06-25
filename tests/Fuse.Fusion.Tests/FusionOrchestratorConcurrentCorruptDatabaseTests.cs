using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

/// <summary>
///     Verifies that corrupt-database recovery clears only the affected connection pool so concurrent fusions
///     against different repositories do not dispose each other's pooled SQLite connections.
/// </summary>
public sealed class FusionOrchestratorConcurrentCorruptDatabaseTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorConcurrentCorruptDatabaseTests()
    {
        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_ConcurrentCorruptDatabaseRecovery_TwoRepos_BothSucceed()
    {
        var firstDirectory = CreateGitRepoWithSource(
            "Alpha.cs",
            """
            public class Alpha
            {
                public string Name => "alpha-token";
            }
            """);
        var secondDirectory = CreateGitRepoWithSource(
            "Beta.cs",
            """
            public class Beta
            {
                public int Value => 42;
            }
            """);

        WriteCorruptDatabase(firstDirectory);
        WriteCorruptDatabase(secondDirectory);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var firstTask = orchestrator.FuseAsync(BuildQueryRequest(firstDirectory, "Alpha", usePersistentIndex: true));
        var secondTask = orchestrator.FuseAsync(BuildQueryRequest(secondDirectory, "Beta", usePersistentIndex: true));

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.NotNull(results[0].InMemoryContent);
        Assert.NotNull(results[1].InMemoryContent);
        Assert.Contains("Alpha.cs", results[0].InMemoryContent);
        Assert.Contains("Beta.cs", results[1].InMemoryContent);
        Assert.True(File.Exists(SqliteTestHelpers.FuseDatabasePath(firstDirectory)));
        Assert.True(File.Exists(SqliteTestHelpers.FuseDatabasePath(secondDirectory)));
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

    private static void WriteCorruptDatabase(string sourceDirectory)
    {
        var fuseDirectory = Path.Combine(sourceDirectory, ".fuse");
        Directory.CreateDirectory(fuseDirectory);
        File.WriteAllText(Path.Combine(fuseDirectory, "fuse.db"), "not a sqlite database");
    }

    private string CreateGitRepoWithSource(string fileName, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-concurrent-corrupt-db-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SqliteTestHelpers.InitializeGitRepository(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();

        SqliteConnection.ClearAllPools();

        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory))
                continue;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
