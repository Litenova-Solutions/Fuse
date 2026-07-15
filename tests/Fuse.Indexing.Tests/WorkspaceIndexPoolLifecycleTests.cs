using System.Collections.Concurrent;
using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class WorkspaceIndexPoolLifecycleTests
{
    [Fact]
    public async Task Parallel_owned_store_lifecycles_release_each_database_before_its_directory_is_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-pool-lifecycle", Guid.NewGuid().ToString("N"));
        var failures = new ConcurrentQueue<Exception>();
        var databasePaths = Enumerable.Range(0, 64)
            .Select(index => Path.Combine(root, index.ToString(), "fuse.db"))
            .ToArray();

        try
        {
            await Parallel.ForEachAsync(
                databasePaths,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(16, Environment.ProcessorCount)) },
                async (databasePath, cancellationToken) =>
                {
                    try
                    {
                        await using (var store = new WorkspaceIndexStore(databasePath))
                        {
                            await store.InitializeAsync(cancellationToken);
                            await store.UpsertFilesAsync(
                                [new IndexedFileRecord("src/Widget.cs", "src/Widget.cs", ".cs", 1, 0, "hash")],
                                cancellationToken);
                            Assert.Equal(1, (await store.GetStateAsync(cancellationToken)).FileCount);
                        }

                        Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue(exception);
                    }
                });

            Assert.Empty(failures);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Corrupt_recovery_clears_only_the_affected_database_pool()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "fuse-pool-recovery", Guid.NewGuid().ToString("N"));
        var recoveredDatabasePath = Path.Combine(root, "recovered", "fuse.db");
        var peerDatabasePath = Path.Combine(root, "peer", "fuse.db");
        var peerFactory = new WorkspaceIndexConnectionFactory(peerDatabasePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(peerDatabasePath)!);
            await using (var peerConnection = await peerFactory.OpenAsync(CancellationToken.None))
            {
                await new IndexSchemaMigrator(peerFactory).PrepareDatabaseAsync(peerConnection, CancellationToken.None);
                await IndexSchemaMigrator.MigrateAsync(peerConnection, CancellationToken.None);
                await IndexSchemaMigrator.EnsureTablesAsync(peerConnection, CancellationToken.None);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(recoveredDatabasePath)!);
            await File.WriteAllTextAsync(recoveredDatabasePath, "not a sqlite database");
            await using (var recoveredStore = new WorkspaceIndexStore(recoveredDatabasePath))
            {
                var outcome = await recoveredStore.InitializeAsync(CancellationToken.None);
                Assert.True(outcome.RebuiltEmptyStore);
            }

            var peerDelete = Record.Exception(() => Directory.Delete(Path.GetDirectoryName(peerDatabasePath)!, recursive: true));
            Assert.IsType<IOException>(peerDelete);
        }
        finally
        {
            peerFactory.ClearPool();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parallel_test_and_index_sources_do_not_clear_every_sqlite_pool()
    {
        var forbiddenCall = string.Concat("Clear", "AllPools", "(");
        var root = FindRepositoryRoot();

        foreach (var directory in EnumerateSourceRoots(root))
        {
            foreach (var sourcePath in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (sourcePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || sourcePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                Assert.DoesNotContain(forbiddenCall, File.ReadAllText(sourcePath));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Fuse.slnx")))
                    return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the Fuse repository root.");
    }

    private static IEnumerable<string> EnumerateSourceRoots(string root)
    {
        yield return Path.Combine(root, "src");

        var testsRoot = Path.Combine(root, "tests");
        foreach (var directory in Directory.EnumerateDirectories(testsRoot, "Fuse.*"))
            yield return directory;

        var benchmarksRoot = Path.Combine(testsRoot, "benchmarks");
        foreach (var directory in Directory.EnumerateDirectories(benchmarksRoot, "Fuse.*"))
            yield return directory;
    }
}
