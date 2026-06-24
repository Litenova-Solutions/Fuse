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
    /// </remarks>
    public static IServiceCollection AddOnnxDenseReranker(this IServiceCollection services)
    {
        if (!RerankModelLocator.IsModelPresent())
            return services;

        services.AddSingleton<IReranker>(provider => new OnnxDenseReranker(
            RerankModelLocator.OnnxModelPath(),
            RerankModelLocator.VocabPath(),
            provider.GetService<ILogger<OnnxDenseReranker>>()));

        return services;
    }
}
