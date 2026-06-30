using Fuse.Plugins.Abstractions.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     Dependency-injection registration for the ONNX dense reranker.
/// </summary>
public static class RerankServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="OnnxDenseReranker" /> as the <see cref="IReranker" /> when its model is present
    ///     in the user-data cache; otherwise registers nothing, so the orchestrator receives no reranker and the
    ///     query path stays on the lexical BM25F floor.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services" /> instance for chaining.</returns>
    /// <remarks>
    ///     Registration is gated on file presence rather than always registering, so a build with no model
    ///     downloaded is byte-identical in behavior to one without the assembly. The reranker still loads the
    ///     model lazily on first use and degrades to lexical if that load fails.
    ///     <para>
    ///         <c>FUSE_RERANK_MODEL=cross</c> selects the cross-encoder (<see cref="OnnxCrossEncoderReranker" />)
    ///         when its model is present; the default <c>bi</c> uses the bi-encoder. The cross-encoder is more
    ///         accurate per query-document pair but runs the model once per candidate, so it is the slower arm.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddOnnxDenseReranker(this IServiceCollection services)
    {
        var useCrossEncoder = string.Equals(
            Environment.GetEnvironmentVariable("FUSE_RERANK_MODEL"), "cross", StringComparison.OrdinalIgnoreCase);

        if (useCrossEncoder)
        {
            if (!RerankModelLocator.IsModelPresent(RerankModelLocator.CrossEncoderModelId))
                return services;

            services.AddSingleton<IReranker>(provider => new OnnxCrossEncoderReranker(
                RerankModelLocator.OnnxModelPath(RerankModelLocator.CrossEncoderModelId),
                RerankModelLocator.VocabPath(RerankModelLocator.CrossEncoderModelId),
                provider.GetService<ILogger<OnnxCrossEncoderReranker>>()));

            return services;
        }

        if (!RerankModelLocator.IsModelPresent())
            return services;

        services.AddSingleton<IReranker>(provider => new OnnxDenseReranker(
            RerankModelLocator.OnnxModelPath(),
            RerankModelLocator.VocabPath(),
            provider.GetService<ILogger<OnnxDenseReranker>>()));

        return services;
    }

    /// <summary>
    ///     Registers <see cref="OnnxTextEmbedder" /> as the <see cref="ITextEmbedder" /> so the indexer persists
    ///     a vector per chunk and the dense retrieval channel is on by default. Registers nothing only when the
    ///     dense channel is explicitly opted out of (<c>FUSE_DENSE</c> falsy), in which case indexing and
    ///     retrieval stay byte-identical to a build without dense.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services" /> instance for chaining.</returns>
    /// <remarks>
    ///     Dense is on by default: the bundled embedding model is fetched once and cached on first index (see
    ///     <see cref="DenseModelProvisioner" />), and all query-time work is offline. The embedder is registered
    ///     even when the model file is not yet present, because it loads lazily and reports unavailable until the
    ///     model is cached, so registration is safe before provisioning runs. When the model is genuinely absent
    ///     (offline, fetch blocked) the embedder stays unavailable and the deterministic lexical path is the
    ///     graceful fallback. The no-rewrite rule is unchanged: the query string is embedded, never paraphrased.
    /// </remarks>
    public static IServiceCollection AddOnnxTextEmbedder(this IServiceCollection services)
    {
        if (!DenseModelProvisioner.IsDenseEnabled)
            return services;

        services.AddSingleton<ITextEmbedder>(provider =>
            OnnxTextEmbedder.CreateDefault(provider.GetService<ILogger<OnnxTextEmbedder>>()));

        return services;
    }
}
