using Fuse.Fusion.Scoping;
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
}
