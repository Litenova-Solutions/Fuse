using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     Ensures the bundled dense embedding model is present in the user-data cache so the dense retrieval
///     channel is on by default. The model is fetched once and cached on first index; every later run, and
///     all query-time work, is fully offline.
/// </summary>
/// <remarks>
///     This is the "fetch-once-and-cache on first run" half of the dense-by-default policy. It runs only on the
///     indexing path (never at query time, so the no-network-at-query-time rule holds), is idempotent (a present
///     model is a fast no-op), and degrades gracefully: when the fetch fails (genuinely offline, or a blocked
///     download) it logs and returns, leaving retrieval on the deterministic lexical fallback. It honors the
///     opt-out (<c>FUSE_DENSE</c> set to a falsy value), so a caller who does not want the dense channel pays no
///     download. The integrity-pinned <see cref="RerankModelDownloader" /> backs the fetch, so a truncated or
///     tampered download is rejected rather than loaded.
/// </remarks>
public static class DenseModelProvisioner
{
    /// <summary>
    ///     Whether the dense channel is enabled. Dense is on by default; it is disabled only when
    ///     <c>FUSE_DENSE</c> is explicitly set to a falsy value (<c>0</c>, <c>false</c>, <c>no</c>, or <c>off</c>,
    ///     case-insensitive). This is the retired no-model floor: absent an explicit opt-out, Fuse provisions and
    ///     uses the bundled embedding model.
    /// </summary>
    public static bool IsDenseEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("FUSE_DENSE");
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return !(value.Equals("0", StringComparison.Ordinal)
                     || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("off", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    ///     Ensures the default bi-encoder embedding model is cached, fetching it once if absent. Safe to call on
    ///     every index: it returns immediately when dense is disabled or the model is already present, and never
    ///     throws (a failed fetch leaves the lexical fallback in place).
    /// </summary>
    /// <param name="progress">An optional sink for human-readable download progress lines.</param>
    /// <param name="logger">An optional logger; a failed fetch is logged once at warning level.</param>
    /// <param name="cancellationToken">A token to cancel the download.</param>
    /// <returns><see langword="true" /> when the model is present after the call; otherwise <see langword="false" />.</returns>
    public static async Task<bool> EnsureModelAsync(
        IProgress<string>? progress = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;

        if (!IsDenseEnabled)
            return false;
        if (RerankModelLocator.IsModelPresent())
            return true;

        try
        {
            progress?.Report("dense: fetching the embedding model once (about 23 MB); later runs are offline.");
            await RerankModelDownloader.DownloadAsync(progress, cancellationToken);
            return RerankModelLocator.IsModelPresent();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline or a blocked download must not break indexing: stay on the lexical fallback.
            logger.LogWarning(ex, "Dense model fetch failed; retrieval stays on the lexical fallback.");
            progress?.Report("dense: model fetch failed; retrieval stays on the lexical fallback.");
            return false;
        }
    }
}
