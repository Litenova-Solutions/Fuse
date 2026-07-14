using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// N6: the freshness contract. A warm index reconciles dirty known files before a read serves data, so no read
// tool answers from an index frozen at first call. A bulk change degrades to a stale-as-of stamp instead of
// reconciling one file at a time.
public sealed class FreshnessReconcileTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-freshness-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "fuse-freshness-db", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "A.cs"), "namespace Demo; public class Alpha { public void M() {} }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "namespace Demo; public class Beta { }");
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await CreateIndexer().IndexAsync(_root, _store, CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_refreshes_an_edited_file_and_removes_a_deleted_file()
    {
        Assert.Equal(0, await CountSymbolNamedAsync("Gamma"));
        Assert.True(await CountSymbolNamedAsync("Beta") > 0);

        // Simulate an out-of-band edit (as an editor session would): add a type to A.cs and delete B.cs.
        File.WriteAllText(Path.Combine(_root, "A.cs"), "namespace Demo; public class Alpha { public void M() {} } public class Gamma { }");
        File.Delete(Path.Combine(_root, "B.cs"));

        var result = await CreateIndexer().ReconcileDirtyFilesAsync(_root, _store, CancellationToken.None);

        Assert.True(result.IsFresh);
        Assert.False(result.Stamped);
        Assert.Equal(2, result.Reconciled); // A.cs edited, B.cs deleted
        // Fresh data: the added type is found, the deleted file's type is gone.
        Assert.True(await CountSymbolNamedAsync("Gamma") > 0);
        Assert.Equal(0, await CountSymbolNamedAsync("Beta"));
    }

    [Fact]
    public async Task Reconcile_is_a_noop_when_nothing_changed()
    {
        var result = await CreateIndexer().ReconcileDirtyFilesAsync(_root, _store, CancellationToken.None);
        Assert.True(result.IsFresh);
        Assert.False(result.Stamped);
        Assert.Equal(0, result.Reconciled);
        Assert.Equal("0", await _store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, CancellationToken.None));
    }

    [Fact]
    public async Task Reconcile_stamps_stale_when_more_than_three_hundred_files_are_dirty()
    {
        const int fileCount = 301;
        var root = Path.Combine(Path.GetTempPath(), "fuse-freshness-storm", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(Path.GetTempPath(), "fuse-freshness-storm-db", Guid.NewGuid().ToString("N"), "fuse.db");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        try
        {
            for (var i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(root, $"T{i}.cs"), $"namespace Storm; public class Type{i} {{ }}");

            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            await CreateIndexer().IndexAsync(root, store, CancellationToken.None);

            for (var i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(root, $"T{i}.cs"), $"namespace Storm; public class Type{i} {{ public void M() {{ }} }}");

            var result = await CreateIndexer().ReconcileDirtyFilesAsync(root, store, CancellationToken.None);

            Assert.False(result.IsFresh);
            Assert.True(result.Stamped);
            Assert.Equal(fileCount, result.Checked);
            Assert.Equal(0, result.Reconciled);
            Assert.Equal(fileCount, result.DirtyRemaining);
            Assert.Equal(
                fileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={databasePath}"));
            foreach (var dir in new[] { root, Path.GetDirectoryName(databasePath)! })
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task<long> CountSymbolNamedAsync(string name) =>
        await ScalarAsync("SELECT count(*) FROM symbols WHERE name = $n;", ("$n", name));

    private async Task<long> ScalarAsync(string sql, params (string, string)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, paramValue) in parameters)
            command.Parameters.AddWithValue(key, paramValue);
        return await command.ExecuteScalarAsync(CancellationToken.None) is long result ? result : 0;
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline, new FileHashService()),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            SemanticAnalysisRunner.CreateDefault());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
        foreach (var dir in new[] { _root, Path.GetDirectoryName(_databasePath)! })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
