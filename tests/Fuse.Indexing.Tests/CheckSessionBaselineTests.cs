using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// S2: the check-session baseline persists to the store so a restarted process resumes the session with its
// baseline intact. These pin save/read, restart resume (reopen the store on the same file), and replace.
public sealed class CheckSessionBaselineTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-check-session-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    private static CheckDiagnostic Diag(string id, string message) =>
        new(id, "Error", message, "src/A.cs", 5);

    [Fact]
    public async Task Saves_and_reads_back_a_baseline()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        await store.SaveCheckSessionBaselineAsync(
            "sess-1", "/repo", [Diag("CS1061", "no member Foo"), Diag("CS0246", "type X")], CancellationToken.None);

        var baseline = await store.GetCheckSessionBaselineAsync("sess-1", CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal("/repo", baseline!.Root);
        Assert.Equal(2, baseline.Diagnostics.Count);
        Assert.Contains(baseline.Diagnostics, d => d.Id == "CS1061" && d.Message == "no member Foo");
        Assert.False(string.IsNullOrEmpty(baseline.UpdatedUtc));
    }

    [Fact]
    public async Task An_unknown_session_returns_null()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Null(await store.GetCheckSessionBaselineAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task A_restarted_process_resumes_the_baseline()
    {
        // First process: write the baseline, then dispose (simulating the agent's process ending).
        await using (var first = new WorkspaceIndexStore(_databasePath))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.SaveCheckSessionBaselineAsync(
                "sess-restart", "/repo", [Diag("CS0029", "cannot convert")], CancellationToken.None);
        }

        // Second process: a fresh store over the same file must see the persisted baseline.
        await using var second = new WorkspaceIndexStore(_databasePath);
        await second.InitializeAsync(CancellationToken.None);
        var baseline = await second.GetCheckSessionBaselineAsync("sess-restart", CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal("CS0029", Assert.Single(baseline!.Diagnostics).Id);
    }

    [Fact]
    public async Task Saving_again_replaces_the_baseline()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        await store.SaveCheckSessionBaselineAsync("sess-2", "/repo", [Diag("CS1061", "old")], CancellationToken.None);
        await store.SaveCheckSessionBaselineAsync("sess-2", "/repo", [], CancellationToken.None);

        var baseline = await store.GetCheckSessionBaselineAsync("sess-2", CancellationToken.None);
        Assert.NotNull(baseline);
        Assert.Empty(baseline!.Diagnostics);
    }

    [Fact]
    public async Task Lists_sessions_for_a_root_unioning_baselines_and_claim_ledgers()
    {
        // G3: the observability panel lists sessions with a baseline OR a claim ledger, filtered by root. A session
        // with both is listed once with both flags; a session under another root is excluded.
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        await store.SaveCheckSessionBaselineAsync("with-baseline", "/repo", [Diag("CS1061", "x")], CancellationToken.None);
        await store.SaveClaimLedgerAsync("with-claims", "/repo", "[]", CancellationToken.None);
        await store.SaveCheckSessionBaselineAsync("with-both", "/repo", [], CancellationToken.None);
        await store.SaveClaimLedgerAsync("with-both", "/repo", "[]", CancellationToken.None);
        await store.SaveCheckSessionBaselineAsync("other-root", "/elsewhere", [], CancellationToken.None);

        var sessions = await store.ListSessionsAsync("/repo", CancellationToken.None);

        Assert.Equal(3, sessions.Count);
        Assert.DoesNotContain(sessions, s => s.SessionId == "other-root");

        var both = Assert.Single(sessions, s => s.SessionId == "with-both");
        Assert.True(both.HasBaseline);
        Assert.True(both.HasClaims);

        var baselineOnly = Assert.Single(sessions, s => s.SessionId == "with-baseline");
        Assert.True(baselineOnly.HasBaseline);
        Assert.False(baselineOnly.HasClaims);

        var claimsOnly = Assert.Single(sessions, s => s.SessionId == "with-claims");
        Assert.False(claimsOnly.HasBaseline);
        Assert.True(claimsOnly.HasClaims);
    }

    [Fact]
    public async Task Lists_no_sessions_for_a_root_with_none()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Empty(await store.ListSessionsAsync("/repo", CancellationToken.None));
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
