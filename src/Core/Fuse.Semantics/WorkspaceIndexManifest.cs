using System.Security.Cryptography;
using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Records and validates the repository identity and complete file inventory of a warm index.
/// </summary>
public static class WorkspaceIndexManifest
{
    /// <summary>The metadata key containing the normalized repository identity.</summary>
    public const string RootMetaKey = "index_workspace_root";

    /// <summary>The metadata key containing <c>building</c> or <c>ready</c>.</summary>
    public const string StateMetaKey = "index_build_state";

    /// <summary>The metadata key containing the number of files in the complete inventory.</summary>
    public const string FileCountMetaKey = "index_inventory_count";

    /// <summary>The metadata key containing a hash of the complete file inventory.</summary>
    public const string InventoryHashMetaKey = "index_inventory_hash";

    /// <summary>The metadata key containing the completion timestamp for diagnostics.</summary>
    public const string CompletedUtcMetaKey = "index_completed_utc";

    /// <summary>Marks a full index replacement as incomplete before any derived rows are changed.</summary>
    /// <param name="rootDirectory">The canonical repository root.</param>
    /// <param name="store">The index store receiving the build.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the building marker is durable.</returns>
    public static async Task BeginBuildAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        await store.SetMetaAsync(StateMetaKey, "building", cancellationToken);
        await store.SetMetaAsync(RootMetaKey, WorkspaceIdentityResolver.NormalizeKey(rootDirectory), cancellationToken);
    }

    /// <summary>Validates that a stored index is complete and belongs to the requested repository.</summary>
    /// <param name="rootDirectory">The canonical repository root.</param>
    /// <param name="store">The readable index store.</param>
    /// <param name="cancellationToken">A token to cancel the validation.</param>
    /// <returns>The manifest validation result.</returns>
    public static async Task<WorkspaceIndexManifestValidation> ValidateAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var state = await store.GetMetaAsync(StateMetaKey, cancellationToken);
        if (state is null)
            return new WorkspaceIndexManifestValidation(false, "workspace manifest is missing");
        if (!string.Equals(state, "ready", StringComparison.Ordinal))
            return new WorkspaceIndexManifestValidation(false, $"index build state is {state}");

        var expectedRoot = WorkspaceIdentityResolver.NormalizeKey(rootDirectory);
        var storedRoot = await store.GetMetaAsync(RootMetaKey, cancellationToken);
        if (!string.Equals(storedRoot, expectedRoot, StringComparison.Ordinal))
            return new WorkspaceIndexManifestValidation(false, "workspace identity does not match the index");

        var hashes = await store.GetAllFileHashesAsync(cancellationToken);
        var storedCount = await store.GetMetaAsync(FileCountMetaKey, cancellationToken);
        if (!int.TryParse(storedCount, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count != hashes.Count)
        {
            return new WorkspaceIndexManifestValidation(false, "workspace inventory count does not match the index");
        }

        var storedHash = await store.GetMetaAsync(InventoryHashMetaKey, cancellationToken);
        var actualHash = ComputeInventoryHash(hashes);
        if (!string.Equals(storedHash, actualHash, StringComparison.Ordinal))
            return new WorkspaceIndexManifestValidation(false, "workspace inventory hash does not match the index");

        return new WorkspaceIndexManifestValidation(true, "ready");
    }

    /// <summary>
    ///     Verifies the stored rows against the complete scan and publishes the ready marker last.
    /// </summary>
    /// <param name="rootDirectory">The canonical repository root.</param>
    /// <param name="store">The index store containing the completed build.</param>
    /// <param name="files">The complete current file inventory.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the ready marker is durable.</returns>
    public static async Task CompleteAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        IReadOnlyCollection<IndexedFileRecord> files,
        CancellationToken cancellationToken)
    {
        var expected = files.ToDictionary(file => file.NormalizedPath, file => file.ContentHash, StringComparer.Ordinal);
        var actual = await store.GetAllFileHashesAsync(cancellationToken);
        var missing = expected.Keys.Where(path => !actual.ContainsKey(path)).Take(5).ToArray();
        var extra = actual.Keys.Where(path => !expected.ContainsKey(path)).Take(5).ToArray();
        var changed = expected.Where(pair =>
                actual.TryGetValue(pair.Key, out var hash)
                && !string.Equals(pair.Value, hash, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Take(5)
            .ToArray();
        if (expected.Count != actual.Count || missing.Length > 0 || extra.Length > 0 || changed.Length > 0)
        {
            throw new InvalidOperationException(
                $"The completed index does not match the scanned workspace inventory " +
                $"(scanned {expected.Count}, stored {actual.Count}; " +
                $"missing: {RenderPaths(missing)}; extra: {RenderPaths(extra)}; changed: {RenderPaths(changed)}).");
        }

        await store.SetMetaAsync(RootMetaKey, WorkspaceIdentityResolver.NormalizeKey(rootDirectory), cancellationToken);
        await store.SetMetaAsync(
            FileCountMetaKey,
            expected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.SetMetaAsync(InventoryHashMetaKey, ComputeInventoryHash(expected), cancellationToken);
        await store.SetMetaAsync(CompletedUtcMetaKey, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        await store.SetMetaAsync(StateMetaKey, "ready", cancellationToken);
    }

    internal static string ComputeInventoryHash(IReadOnlyDictionary<string, string> hashes)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            incremental.AppendData(Encoding.UTF8.GetBytes(pair.Key));
            incremental.AppendData([0]);
            incremental.AppendData(Encoding.UTF8.GetBytes(pair.Value));
            incremental.AppendData([0]);
        }

        return Convert.ToHexStringLower(incremental.GetHashAndReset());
    }

    private static string RenderPaths(IReadOnlyCollection<string> paths) =>
        paths.Count == 0 ? "none" : string.Join(", ", paths);
}

/// <summary>The result of validating a warm index manifest.</summary>
/// <param name="Ready">Whether the index is complete and belongs to the requested repository.</param>
/// <param name="Detail">A diagnostic description of the result.</param>
public sealed record WorkspaceIndexManifestValidation(bool Ready, string Detail);
