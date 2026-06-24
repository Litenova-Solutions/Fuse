using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Reduction.Tests.Caching;

public sealed class SqliteKeyValueStoreTests : IDisposable
{
    private readonly string _databasePath;

    public SqliteKeyValueStoreTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _databasePath = Path.Combine(root, "fuse.db");
    }

    [Fact]
    public async Task Set_BeforeFlush_IsVisibleToTryGet()
    {
        await using var store = new SqliteKeyValueStore(_databasePath);
        var payload = "read-your-writes"u8.ToArray();

        store.Set("test", "key", payload);

        Assert.True(store.TryGet("test", "key", out var value));
        Assert.Equal(payload, value);
    }

    [Fact]
    public async Task FlushAsync_PersistsAcrossInstances()
    {
        await using (var writer = new SqliteKeyValueStore(_databasePath))
        {
            writer.Set("batch", "a", [1]);
            writer.Set("batch", "b", [2, 3]);
            await writer.FlushAsync();
        }

        await using var reader = new SqliteKeyValueStore(_databasePath);
        Assert.True(reader.TryGet("batch", "a", out var a));
        Assert.True(reader.TryGet("batch", "b", out var b));
        Assert.Equal([1], a);
        Assert.Equal([2, 3], b);
    }

    [Fact]
    public async Task DisposeAsync_FlushesBufferedWrites()
    {
        {
            await using var store = new SqliteKeyValueStore(_databasePath);
            store.Set("dispose", "key", [9]);
        }

        await using var reader = new SqliteKeyValueStore(_databasePath);
        Assert.True(reader.TryGet("dispose", "key", out var value));
        Assert.Equal([9], value);
    }

    [Fact]
    public async Task CorruptDatabase_IsRecreatedOnNextOpen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        File.WriteAllText(_databasePath, "not a sqlite database");

        await using (var store = new SqliteKeyValueStore(_databasePath))
        {
            store.Set("recovery", "key", [7]);

            Assert.True(store.TryGet("recovery", "key", out var value));
            Assert.Equal([7], value);

            await store.FlushAsync();
        }

        await using var reader = new SqliteKeyValueStore(_databasePath);
        Assert.True(reader.TryGet("recovery", "key", out var persisted));
        Assert.Equal([7], persisted);
    }

    [Fact]
    public async Task Clear_RemovesEntriesFromStore()
    {
        await using var store = new SqliteKeyValueStore(_databasePath);
        store.Set("clear-me", "one", [1]);
        store.Clear("clear-me");

        Assert.False(store.TryGet("clear-me", "one", out _));
    }

    [Fact]
    public async Task FlushAsync_ConcurrentWriters_BothPersist()
    {
        var writeA = Task.Run(async () =>
        {
            await using var store = new SqliteKeyValueStore(_databasePath);
            store.Set("parallel", "a", [1]);
            await store.FlushAsync();
        });
        var writeB = Task.Run(async () =>
        {
            await using var store = new SqliteKeyValueStore(_databasePath);
            store.Set("parallel", "b", [2, 3]);
            await store.FlushAsync();
        });

        await Task.WhenAll(writeA, writeB);

        await using var reader = new SqliteKeyValueStore(_databasePath);
        Assert.True(reader.TryGet("parallel", "a", out var a));
        Assert.True(reader.TryGet("parallel", "b", out var b));
        Assert.Equal([1], a);
        Assert.Equal([2, 3], b);
    }

    [Fact]
    public async Task FlushAsync_SetDuringFlush_DoesNotDropTheConcurrentUpdate()
    {
        // C4: FlushAsync snapshots the pending writes, commits them, then removes the flushed keys. A Set that
        // lands on a snapshot key while the commit is in flight must survive: removal is by value, so the newer
        // value stays pending for the next flush instead of being dropped. A large background batch keeps each
        // flush long enough to overlap the hot-key writer.
        await using var store = new SqliteKeyValueStore(_databasePath);
        for (var i = 0; i < 4000; i++)
            store.Set("bulk", $"k{i}", BitConverter.GetBytes(i));

        const int iterations = 300;
        var setter = Task.Run(async () =>
        {
            for (var v = 1; v <= iterations; v++)
            {
                store.Set("hot", "key", BitConverter.GetBytes(v));
                await Task.Yield();
            }
        });
        var flusher = Task.Run(async () =>
        {
            for (var f = 0; f < iterations; f++)
                await store.FlushAsync();
        });

        await Task.WhenAll(setter, flusher);
        await store.FlushAsync();

        // The last value the setter wrote is never lost: it is either flushed or still pending, but retrievable.
        await using var reader = new SqliteKeyValueStore(_databasePath);
        Assert.True(reader.TryGet("hot", "key", out var value));
        Assert.Equal(iterations, BitConverter.ToInt32(value!));
    }

    [Fact]
    public async Task FlushAsync_WhenDatabaseIsBusy_RetriesAndSucceeds()
    {
        await using (var initializer = new SqliteKeyValueStore(_databasePath))
        {
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString();

        await using var blocker = new SqliteConnection(connectionString);
        await blocker.OpenAsync();
        await using (var beginCommand = blocker.CreateCommand())
        {
            beginCommand.CommandText = "BEGIN IMMEDIATE";
            await beginCommand.ExecuteNonQueryAsync();
        }

        var flushTask = Task.Run(async () =>
        {
            await using var store = new SqliteKeyValueStore(_databasePath);
            store.Set("contention", "entry", [5, 6]);
            await store.FlushAsync();
        });

        await Task.Delay(150);

        await using (var rollbackCommand = blocker.CreateCommand())
        {
            rollbackCommand.CommandText = "ROLLBACK";
            await rollbackCommand.ExecuteNonQueryAsync();
        }

        await flushTask.WaitAsync(TimeSpan.FromSeconds(15));

        await using var reader = new SqliteKeyValueStore(_databasePath);
        Assert.True(reader.TryGet("contention", "entry", out var value));
        Assert.Equal([5, 6], value);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_databasePath);
        if (root is null || !Directory.Exists(root))
            return;

        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }
}
