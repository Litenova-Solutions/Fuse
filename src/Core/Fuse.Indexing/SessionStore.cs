using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fuse.Indexing;

/// <summary>
///     Persists check-session baselines and claim ledgers in <c>check_sessions</c> and <c>claim_ledger</c>.
/// </summary>
internal sealed class SessionStore
{
    private readonly WorkspaceIndexConnectionFactory _connectionFactory;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SessionStore" /> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory for the index database.</param>
    public SessionStore(WorkspaceIndexConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    /// <summary>
    ///     Saves or replaces a check-session baseline.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="root">The workspace root path.</param>
    /// <param name="baseline">The baseline diagnostics.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the baseline is persisted.</returns>
    public async Task SaveCheckSessionBaselineAsync(
        string sessionId, string root, IReadOnlyList<CheckDiagnostic> baseline, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            baseline.ToList(), BuildCaptureJsonContext.Default.ListCheckDiagnostic);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO check_sessions(session_id, root, baseline_json, updated_utc) VALUES($id, $root, $json, $utc) " +
            "ON CONFLICT(session_id) DO UPDATE SET root = excluded.root, baseline_json = excluded.baseline_json, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Reads a persisted check-session baseline.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The baseline, or <see langword="null" /> when absent.</returns>
    public async Task<CheckSessionBaseline?> GetCheckSessionBaselineAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT root, baseline_json, updated_utc FROM check_sessions WHERE session_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var root = reader.GetString(0);
        var json = reader.GetString(1);
        var updatedUtc = reader.GetString(2);
        var diagnostics = JsonSerializer.Deserialize(json, BuildCaptureJsonContext.Default.ListCheckDiagnostic)
            ?? [];
        return new CheckSessionBaseline(sessionId, root, diagnostics, updatedUtc);
    }

    /// <summary>
    ///     Saves or replaces a claim ledger for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="root">The workspace root path.</param>
    /// <param name="claimsJson">The serialized claims payload.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the ledger is persisted.</returns>
    public async Task SaveClaimLedgerAsync(string sessionId, string root, string claimsJson, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO claim_ledger(session_id, root, claims_json, updated_utc) VALUES($id, $root, $json, $utc) " +
            "ON CONFLICT(session_id) DO UPDATE SET root = excluded.root, claims_json = excluded.claims_json, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$json", claimsJson);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Reads a persisted claim ledger.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ledger record, or <see langword="null" /> when absent.</returns>
    public async Task<ClaimLedgerRecord?> GetClaimLedgerAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT root, claims_json, updated_utc FROM claim_ledger WHERE session_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ClaimLedgerRecord(sessionId, reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    /// <summary>
    ///     Lists sessions for a workspace root, unioning baselines and claim ledgers.
    /// </summary>
    /// <param name="root">The workspace root path.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>Session summaries ordered by most recent update.</returns>
    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string root, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id, MAX(updated_utc) AS updated_utc, MAX(has_baseline) AS has_baseline, MAX(has_claims) AS has_claims FROM (" +
            "  SELECT session_id, updated_utc, 1 AS has_baseline, 0 AS has_claims FROM check_sessions WHERE root = $root" +
            "  UNION ALL" +
            "  SELECT session_id, updated_utc, 0 AS has_baseline, 1 AS has_claims FROM claim_ledger WHERE root = $root" +
            ") GROUP BY session_id ORDER BY updated_utc DESC;";
        command.Parameters.AddWithValue("$root", root);

        var sessions = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(3) != 0));
        }

        return sessions;
    }
}
