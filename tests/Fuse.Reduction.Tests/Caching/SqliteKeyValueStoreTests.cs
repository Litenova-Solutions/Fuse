using Fuse.Reduction.Caching;

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

        await using var store = new SqliteKeyValueStore(_databasePath);
        store.Set("recovery", "key", [7]);

        Assert.True(store.TryGet("recovery", "key", out var value));
        Assert.Equal([7], value);
    }

    [Fact]
    public async Task Clear_RemovesEntriesFromStore()
    {
        await using var store = new SqliteKeyValueStore(_databasePath);
        store.Set("clear-me", "one", [1]);
        store.Clear("clear-me");

        Assert.False(store.TryGet("clear-me", "one", out _));
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_databasePath);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
