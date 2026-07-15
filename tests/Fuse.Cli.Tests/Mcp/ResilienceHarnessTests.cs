using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R32: a standing fault-injection harness. For each injected fault, the invariant holds - the tool degrades to a
// stable operational prefix or a graded answer, never an unhandled exception (crash) and never silent-empty. The
// count rises by one case per fault; a regression that removes a degradation path fails the matching case.
public sealed class ResilienceHarnessTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;

    public ResilienceHarnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-resilience", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git")); // isolate the store under this root.
        _databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
    }

    // Fault: every expected operational failure maps to a stable prefix through the MCP boundary, never a throw.
    [Theory]
    [InlineData("busy")]
    [InlineData("locked")]
    [InlineData("missing_table")]
    [InlineData("sharing_violation")]
    [InlineData("search_unavailable")]
    [InlineData("rebuilding")]
    [InlineData("validation")]
    [InlineData("unknown")]
    public async Task Fault_OperationalBoundary_DegradesToStablePrefix_NeverThrows(string fault)
    {
        Exception injected = fault switch
        {
            "busy" => SqliteError(5),
            "locked" => SqliteError(6),
            "missing_table" => MissingTableSqliteError(),
            "sharing_violation" => new IOException("in use", unchecked((int)0x80070020)),
            "search_unavailable" => new SearchIndexUnavailableException("search index missing; rebuilding."),
            "rebuilding" => new IndexRebuildingException("after upgrade"),
            "validation" => new ArgumentException("bad arg"),
            _ => new InvalidOperationException("boom"),
        };

        // ExecuteMcpAsync must convert the fault to a prefixed string and never rethrow.
        var result = await FuseOperationalErrors.ExecuteMcpAsync(() => throw injected);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Matches(@"^(index_busy:|index_not_built:|workspace_not_found:|validation_error:|index_rebuilding:|internal_error:)", result);
        Assert.DoesNotContain("Unhandled", result, StringComparison.OrdinalIgnoreCase);
    }

    // Fault: a corrupt fuse.db mid-open is derived data - it recovers to a working store, no crash, no silent-empty.
    [Fact]
    public async Task Fault_CorruptDatabase_RecoversToWorkingStore()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
    }

    // Fault: an extraction-version-skewed store rebuilds rather than serving stale/empty (R22).
    [Fact]
    public async Task Fault_ExtractionVersionSkew_Rebuilds()
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync([new IndexedFileRecord("a.cs", "a.cs", ".cs", 1, 0, "h", Language: "csharp")], CancellationToken.None);
            await seed.SetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, "0", CancellationToken.None);
        }

        await using var reopened = new WorkspaceIndexStore(_databasePath);
        Assert.True((await reopened.InitializeAsync(CancellationToken.None)).RebuiltEmptyStore);
    }

    // Fault: a search against a store missing chunk_fts surfaces an operational signal, never a raw SQLite error.
    [Fact]
    public async Task Fault_MissingSearchTable_OperationalPrefix_NotInternalError()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertFilesAsync([new IndexedFileRecord("a.cs", "a.cs", ".cs", 1, 0, "h", Language: "csharp")], CancellationToken.None);
        await store.UpsertChunksAsync([new ChunkRecord("c", "a.cs", "type", "sk", 1, 2, "th", 1, 1, Name: "A", Body: "class A{}", SymbolsText: "A")], CancellationToken.None);
        using (var conn = new SqliteConnection($"Data Source={_databasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS chunk_fts;";
            cmd.ExecuteNonQuery();
        }

        var message = await FuseOperationalErrors.ExecuteMcpAsync(async () =>
        {
            await store.SearchAsync(new SearchQuery("A", 5), CancellationToken.None);
            return "ok";
        });

        Assert.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, message);
        Assert.DoesNotContain("SQLite Error", message, StringComparison.Ordinal);
    }

    // Fault: an integrity-violating store (symbols, zero chunks, FTS available) is never reported ready (R31).
    [Fact]
    public void Fault_IntegrityViolation_NeverHealthy()
    {
        var broken = new WorkspaceIndexState(WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Warm, 5, 10, "semantic", FtsAvailable: true, ChunkCount: 0);
        Assert.False(IndexIntegrity.Check(broken).Healthy);
    }

    private static SqliteException SqliteError(int code) => MakeSqlite(code, $"SQLITE error {code}");

    private static SqliteException MissingTableSqliteError()
    {
        // Trigger a real SQLITE_ERROR (code 1) with a "no such table" message.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM does_not_exist;";
        return Assert.Throws<SqliteException>(() => cmd.ExecuteReader());
    }

    private static SqliteException MakeSqlite(int code, string message)
    {
        // Provoke a genuine SqliteException with the desired primary error code via a constraint/lock path is
        // fiddly; construct directly (SqliteException has a public constructor taking message and error code).
        return new SqliteException(message, code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
