using Fuse.Fusion.Indexing;
using Fuse.Fusion.Scoping;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Tests.Indexing;

public sealed class RelevancePostingsStoreTests : IDisposable
{
    // Counts body tokenizations by wrapping a real SQLite store, so a test can assert only changed files are
    // re-tokenized on a warm run.
    private sealed class CountingStore(IRelevancePostingsStore inner) : IRelevancePostingsStore
    {
        public int Misses { get; private set; }

        public bool TryGetBodyTokens(ulong contentHash, out IReadOnlyList<string> tokens)
        {
            if (inner.TryGetBodyTokens(contentHash, out tokens))
                return true;
            Misses++;
            return false;
        }

        public void SetBodyTokens(ulong contentHash, IReadOnlyList<string> tokens) =>
            inner.SetBodyTokens(contentHash, tokens);
    }

    private readonly string _databasePath;

    public RelevancePostingsStoreTests() =>
        _databasePath = SqliteTestHelpers.NewDatabasePath("fuse-postings-tests");

    private static Dictionary<string, IndexedDocument> Docs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Order.cs"] = new IndexedDocument("public class Order { public void Place() { var total = ComputeTotal(); } }", "Order.cs", ["Order", "Place"]),
        ["Payment.cs"] = new IndexedDocument("public class Payment { public void Charge() { } }", "Payment.cs", ["Payment", "Charge"]),
    };

    [Fact]
    public async Task PersistedIndex_RanksIdenticallyToInMemory()
    {
        var docs = Docs();

        var inMemory = new Bm25RelevanceIndex();
        inMemory.Index(docs);
        var inMemoryRanked = inMemory.RankScored("place order total", 10);

        await using var store = new SqliteKeyValueStore(_databasePath);
        var persisted = new Bm25RelevanceIndex();
        persisted.Index(docs, new SqliteRelevancePostingsStore(store));
        var persistedRanked = persisted.RankScored("place order total", 10);

        Assert.Equal(inMemoryRanked.Select(r => r.Path), persistedRanked.Select(r => r.Path));
        Assert.Equal(inMemoryRanked.Select(r => r.Score), persistedRanked.Select(r => r.Score));
    }

    [Fact]
    public async Task WarmRun_ReTokenizesOnlyChangedFiles()
    {
        await using var store = new SqliteKeyValueStore(_databasePath);
        var sqliteStore = new CountingStore(new SqliteRelevancePostingsStore(store));

        var docs = Docs();
        new Bm25RelevanceIndex().Index(docs, sqliteStore);
        Assert.Equal(2, sqliteStore.Misses); // cold: both bodies tokenized

        // Edit one file; the other is unchanged. A warm run must re-tokenize only the changed file.
        docs["Order.cs"] = new IndexedDocument("public class Order { public void Place() { var total = 0; } }", "Order.cs", ["Order", "Place"]);
        new Bm25RelevanceIndex().Index(docs, sqliteStore);
        Assert.Equal(3, sqliteStore.Misses); // one new miss for the edited body; Payment.cs hit
    }

    [Fact]
    public async Task StaleEntry_IsInvalidatedByContentHash()
    {
        await using var store = new SqliteKeyValueStore(_databasePath);
        var sqliteStore = new SqliteRelevancePostingsStore(store);
        var original = "public class A { public void M() { Foo(); } }";
        var hashStore = new CountingStore(sqliteStore);

        var docs = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = new IndexedDocument(original, "A.cs", ["A", "M"]),
        };
        new Bm25RelevanceIndex().Index(docs, hashStore);
        Assert.Equal(1, hashStore.Misses);

        // Changed content -> different hash -> miss (the old entry is never served for the new content).
        docs["A.cs"] = new IndexedDocument("public class A { public void M() { Bar(); } }", "A.cs", ["A", "M"]);
        new Bm25RelevanceIndex().Index(docs, hashStore);
        Assert.Equal(2, hashStore.Misses);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_databasePath);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
