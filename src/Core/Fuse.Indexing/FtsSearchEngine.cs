using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Fuse.Indexing;

/// <summary>
///     FTS5 availability probing, chunk indexing, and ranked full-text search over <c>chunk_fts</c>.
/// </summary>
internal sealed class FtsSearchEngine
{
    private readonly WorkspaceIndexConnectionFactory _connectionFactory;
    private readonly ILogger? _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FtsSearchEngine" /> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory for the index database.</param>
    /// <param name="logger">An optional logger for FTS diagnostics.</param>
    public FtsSearchEngine(WorkspaceIndexConnectionFactory connectionFactory, ILogger? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    ///     Whether full-text search was probed available on the last initialization.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    ///     Probes FTS5 availability by creating the virtual table.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the probe.</param>
    /// <returns><see langword="true" /> when FTS5 is available.</returns>
    public async Task<bool> TryCreateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = WorkspaceIndexSchema.CreateFtsDdl;
            await command.ExecuteNonQueryAsync(cancellationToken);
            Available = true;
            return true;
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex, "FTS5 unavailable at {DatabasePath}; full-text search disabled.", _connectionFactory.DatabasePath);
            Available = false;
            return false;
        }
    }

    /// <summary>
    ///     Marks FTS availability without probing (read-only warm open).
    /// </summary>
    /// <param name="available">Whether FTS is available.</param>
    public void MarkAvailable(bool available) => Available = available;

    /// <summary>
    ///     Runs a ranked FTS query over indexed chunks.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="cancellationToken">A token to cancel the search.</param>
    /// <returns>Ranked search hits, or an empty list when FTS is unavailable.</returns>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (!Available || string.IsNullOrWhiteSpace(query.Text))
            return [];

        var match = BuildMatchExpression(query.Text);
        if (match.Length == 0)
            return [];

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.chunk_id, files.normalized_path, c.kind, c.name, c.start_line, c.end_line,
                   -bm25(chunk_fts, 0.0, 2.0, 5.0, 4.0, 3.0, 1.5, 1.0, 2.5, 0.7) AS score
            FROM chunk_fts f
            JOIN chunks c ON c.chunk_id = f.chunk_id
            JOIN files ON files.file_id = c.file_id
            WHERE chunk_fts MATCH $match
            ORDER BY score DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", query.Limit);

        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                ChunkId: reader.GetString(0),
                FilePath: reader.GetString(1),
                Kind: reader.GetString(2),
                Name: reader.IsDBNull(3) ? null : reader.GetString(3),
                StartLine: reader.GetInt32(4),
                EndLine: reader.GetInt32(5),
                Score: reader.GetDouble(6)));
        }

        return hits;
    }

    /// <summary>
    ///     Indexes or re-indexes FTS rows for the given chunks inside an existing transaction.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The active transaction.</param>
    /// <param name="chunks">The chunks whose FTS rows to write.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when FTS rows are updated.</returns>
    public async Task IndexChunksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ChunkRecord> chunks,
        CancellationToken cancellationToken)
    {
        if (!Available || chunks.Count == 0)
            return;

        await using var ftsDelete = connection.CreateCommand();
        ftsDelete.Transaction = transaction;
        ftsDelete.CommandText = "DELETE FROM chunk_fts WHERE chunk_id = $id;";
        ftsDelete.Parameters.Add("$id", SqliteType.Text);

        await using var ftsInsert = connection.CreateCommand();
        ftsInsert.Transaction = transaction;
        ftsInsert.CommandText = """
            INSERT INTO chunk_fts(chunk_id, path, name, symbols, signature, comments, body, subtokens, stems)
            VALUES($id, $path, $name, $symbols, $sig, $comments, $body, $subtokens, $stems);
            """;
        ftsInsert.Parameters.Add("$id", SqliteType.Text);
        ftsInsert.Parameters.Add("$path", SqliteType.Text);
        ftsInsert.Parameters.Add("$name", SqliteType.Text);
        ftsInsert.Parameters.Add("$symbols", SqliteType.Text);
        ftsInsert.Parameters.Add("$sig", SqliteType.Text);
        ftsInsert.Parameters.Add("$comments", SqliteType.Text);
        ftsInsert.Parameters.Add("$body", SqliteType.Text);
        ftsInsert.Parameters.Add("$subtokens", SqliteType.Text);
        ftsInsert.Parameters.Add("$stems", SqliteType.Text);

        foreach (var chunk in chunks)
        {
            ftsDelete.Parameters["$id"].Value = chunk.ChunkId;
            await ftsDelete.ExecuteNonQueryAsync(cancellationToken);

            ftsInsert.Parameters["$id"].Value = chunk.ChunkId;
            ftsInsert.Parameters["$path"].Value = chunk.FilePath;
            ftsInsert.Parameters["$name"].Value = (object?)chunk.Name ?? DBNull.Value;
            ftsInsert.Parameters["$symbols"].Value = (object?)chunk.SymbolsText ?? DBNull.Value;
            ftsInsert.Parameters["$sig"].Value = (object?)chunk.Signature ?? DBNull.Value;
            ftsInsert.Parameters["$comments"].Value = (object?)chunk.Comments ?? DBNull.Value;
            ftsInsert.Parameters["$body"].Value = (object?)chunk.Body ?? DBNull.Value;
            var subtokens = IdentifierSplitter.Expand(chunk.Name, chunk.SymbolsText, chunk.Signature);
            ftsInsert.Parameters["$subtokens"].Value = subtokens.Length == 0 ? DBNull.Value : subtokens;
            var stems = PorterStemmer.Expand(chunk.Name, chunk.SymbolsText, chunk.Signature, chunk.Comments);
            ftsInsert.Parameters["$stems"].Value = stems.Length == 0 ? DBNull.Value : stems;
            await ftsInsert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Deletes FTS rows for all chunks owned by a file inside an existing transaction.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The active transaction.</param>
    /// <param name="fileId">The owning file id.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when FTS rows are removed.</returns>
    public async Task DeleteForFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        CancellationToken cancellationToken)
    {
        if (!Available)
            return;

        await using var ftsCommand = connection.CreateCommand();
        ftsCommand.Transaction = transaction;
        ftsCommand.CommandText =
            "DELETE FROM chunk_fts WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE file_id = $file);";
        ftsCommand.Parameters.AddWithValue("$file", fileId);
        await ftsCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Builds a safe FTS5 MATCH expression from free text.
    /// </summary>
    /// <param name="text">The user query text.</param>
    /// <returns>An FTS5 MATCH expression, or an empty string when no terms remain.</returns>
    public static string BuildMatchExpression(string text)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            terms.Add(current.ToString());

        var expanded = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (seen.Add(term))
                expanded.Add(term);
            foreach (var part in IdentifierSplitter.Split(term))
            {
                if (seen.Add(part))
                    expanded.Add(part);
                var stem = PorterStemmer.Stem(part);
                if (seen.Add(stem))
                    expanded.Add(stem);
            }
        }

        return string.Join(" OR ", expanded.Select(t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }
}
