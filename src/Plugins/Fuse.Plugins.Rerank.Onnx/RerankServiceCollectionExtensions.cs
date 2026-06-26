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
    ///     Registers <see cref="OnnxTextEmbedder" /> as the <see cref="ITextEmbedder" /> when dense indexing is
    ///     opted into (<c>FUSE_DENSE</c> truthy) and the bi-encoder model is present in the user-data cache;
    ///     otherwise registers nothing, so indexing persists no embeddings and retrieval stays on the lexical
    ///     floor.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services" /> instance for chaining.</returns>
    /// <remarks>
    ///     Unlike the lazy reranker, the embedder is used eagerly at index time to persist a vector per chunk,
    ///     which is a real indexing cost. It is therefore gated on an explicit opt-in (<c>FUSE_DENSE</c>) in
    ///     addition to model presence, so downloading the model for the reranker alone does not silently slow
    ///     indexing. When unregistered, indexing and retrieval are byte-identical to a build without dense.
    /// </remarks>
    public static IServiceCollection AddOnnxTextEmbedder(this IServiceCollection services)
    {
        if (!IsDenseEnabled() || !RerankModelLocator.IsModelPresent())
            return services;

        services.AddSingleton<ITextEmbedder>(provider =>
            OnnxTextEmbedder.CreateDefault(provider.GetService<ILogger<OnnxTextEmbedder>>()));

        return services;
    }

    // Dense indexing is opt-in: FUSE_DENSE set to 1/true/yes/on (case-insensitive) enables it.
    private static bool IsDenseEnabled()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_DENSE");
        return value is not null
               && (value.Equals("1", StringComparison.Ordinal)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
