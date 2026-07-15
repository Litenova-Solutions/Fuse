using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class SessionStoreIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-session-port-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexConnectionFactory _factory = null!;
    private SessionStore _sessions = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _factory = new WorkspaceIndexConnectionFactory(_databasePath);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        await new IndexSchemaMigrator(_factory).PrepareDatabaseAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.MigrateAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.EnsureTablesAsync(connection, CancellationToken.None);

        _sessions = new SessionStore(_factory);
    }

    [Fact]
    public async Task SaveCheckSessionBaseline_round_trips()
    {
        await _sessions.SaveCheckSessionBaselineAsync(
            "sess-port",
            "/repo",
            [new CheckDiagnostic("CS0001", "Error", "msg", "src/A.cs", 1)],
            CancellationToken.None);

        var baseline = await _sessions.GetCheckSessionBaselineAsync("sess-port", CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal("/repo", baseline!.Root);
        Assert.Equal("CS0001", Assert.Single(baseline.Diagnostics).Id);
    }

    [Fact]
    public async Task ListSessions_unions_baseline_and_claim_ledger()
    {
        await _sessions.SaveCheckSessionBaselineAsync("sess-a", "/repo", [], CancellationToken.None);
        await _sessions.SaveClaimLedgerAsync("sess-b", "/repo", "{}", CancellationToken.None);

        var sessions = await _sessions.ListSessionsAsync("/repo", CancellationToken.None);

        Assert.Contains(sessions, s => s.SessionId == "sess-a" && s.HasBaseline);
        Assert.Contains(sessions, s => s.SessionId == "sess-b" && s.HasClaims);
    }

    public Task DisposeAsync()
    {
        _factory.ClearPool();
        return Task.CompletedTask;
    }
}
