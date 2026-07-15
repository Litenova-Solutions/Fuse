using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Services;

/// <summary>
///     Reads the per-project load diagnosis stamped into <c>index_meta</c> at index time (R43), so
///     <c>fuse_workspace action=doctor</c> and the CLI <c>fuse doctor</c> can report the achieved tier and
///     per-project reasons from the warm index in sub-second time instead of re-running the full MSBuild/Roslyn
///     load. A live load runs only when the caller forces a refresh or no stamp is present.
/// </summary>
public static class PersistedDiagnosisReader
{
    /// <summary>
    ///     Reads the stamped load diagnosis for a workspace root, or returns null when there is no database, no
    ///     stamp (an older index or a store still building), or the store cannot be opened read-only.
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The persisted diagnosis, or null to fall back to a live load.</returns>
    public static async Task<PersistedLoadDiagnosis?> TryReadAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return null;

        try
        {
            await using var store = new WorkspaceIndexStore(databasePath);
            if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
                return null;
            var json = await store.GetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return System.Text.Json.JsonSerializer.Deserialize(
                json, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
