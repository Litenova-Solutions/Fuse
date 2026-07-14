using System.Text.RegularExpressions;
using Xunit;

namespace Fuse.Indexing.Tests;

// R9: grep gate ensuring WorkspaceIndexStore stays a facade and does not embed port SQL.
public sealed class WorkspaceIndexStoreBloatGateTests
{
    private static readonly string StoreSourcePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Core", "Fuse.Indexing", "WorkspaceIndexStore.cs"));

    private static readonly string[] ForbiddenSqlFragments =
    [
        "FROM chunk_fts",
        "FROM check_sessions",
        "FROM claim_ledger",
        "FROM symbols",
        "FROM edges",
        "INSERT INTO files",
        "INSERT INTO chunks",
        "CREATE VIRTUAL TABLE",
        "bm25(chunk_fts",
    ];

    [Fact]
    public void WorkspaceIndexStore_does_not_embed_port_sql()
    {
        Assert.True(File.Exists(StoreSourcePath), $"Missing source file: {StoreSourcePath}");
        var source = File.ReadAllText(StoreSourcePath);

        foreach (var fragment in ForbiddenSqlFragments)
            Assert.DoesNotContain(fragment, source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceIndexStore_delegates_to_internal_ports()
    {
        Assert.True(File.Exists(StoreSourcePath), $"Missing source file: {StoreSourcePath}");
        var source = File.ReadAllText(StoreSourcePath);

        Assert.Contains("IndexSchemaMigrator", source, StringComparison.Ordinal);
        Assert.Contains("FtsSearchEngine", source, StringComparison.Ordinal);
        Assert.Contains("SymbolGraphStore", source, StringComparison.Ordinal);
        Assert.Contains("SessionStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Port_types_exist_as_internal_implementation_files()
    {
        var indexingDir = Path.GetDirectoryName(StoreSourcePath)!;

        foreach (var portFile in new[] { "IndexSchemaMigrator.cs", "FtsSearchEngine.cs", "SymbolGraphStore.cs", "SessionStore.cs" })
        {
            var path = Path.Combine(indexingDir, portFile);
            Assert.True(File.Exists(path), $"Missing port file: {path}");
            var text = File.ReadAllText(path);
            Assert.Matches(new Regex(@"internal\s+sealed\s+class", RegexOptions.None), text);
        }
    }
}
