using Fuse.Fusion.Retrieval;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Embeddings.Onnx.Extensions;

/// <summary>
///     Registers the opt-in ONNX semantic embedding model in place of the hashing default, with a tri-state
///     resolution and an intact fallback.
/// </summary>
public static class OnnxEmbeddingsServiceCollectionExtensions
{
    /// <summary>
    ///     The environment variable that selects the embedding backend: <c>1</c>/<c>true</c> for ONNX,
    ///     <c>0</c>/<c>false</c> for hashing.
    /// </summary>
    public const string EnableVariable = "FUSE_EMBEDDINGS";

    /// <summary>
    ///     Resolves the embedding choice: an explicit flag wins, otherwise <see cref="EnableVariable" /> is
    ///     consulted, otherwise the build default applies (<see langword="null" />). The default is opt-in
    ///     (hashing) until a recall@budget measurement justifies flipping it.
    /// </summary>
    /// <param name="explicitFlag">The value of an explicit <c>--embeddings</c> flag, or <c>null</c> when absent.</param>
    /// <returns><see langword="true" /> for ONNX, <see langword="false" /> for hashing, <see langword="null" /> for the default.</returns>
    public static bool? ResolveChoice(bool? explicitFlag)
    {
        if (explicitFlag.HasValue)
            return explicitFlag;

        var value = Environment.GetEnvironmentVariable(EnableVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    ///     Registers <see cref="OnnxEmbeddingModel" /> as the <see cref="IEmbeddingModel" /> when embeddings are
    ///     requested and the model resolves; otherwise leaves the hashing default in place. A forced-off choice
    ///     and an unavailable model both keep hashing, so a run never fails for want of the model.
    /// </summary>
    /// <param name="services">The service collection. Call after <c>AddFuse</c> so this registration wins.</param>
    /// <param name="explicitFlag">The value of an explicit <c>--embeddings</c> flag, or <c>null</c>.</param>
    /// <param name="descriptor">The model to use, or <c>null</c> for <see cref="EmbeddingModelDescriptor.Default" />.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    public static IServiceCollection AddFuseOnnxEmbeddings(
        this IServiceCollection services,
        bool? explicitFlag,
        EmbeddingModelDescriptor? descriptor = null)
    {
        if (ResolveChoice(explicitFlag) != true)
            return services; // Forced off or default: the hashing embedding registered by AddFuse stays.

        var model = descriptor ?? EmbeddingModelDescriptor.Default;
        services.AddSingleton<IEmbeddingModelDownloader, HttpEmbeddingModelDownloader>();

        // Resolve lazily at first use through a factory: the last IEmbeddingModel registration wins, and the
        // model only loads (and downloads) when something actually reranks.
        services.AddSingleton<IEmbeddingModel>(sp =>
        {
            var downloader = sp.GetRequiredService<IEmbeddingModelDownloader>();
            var resolver = new EmbeddingModelResolver(downloader);

            ResolvedEmbeddingModel? resolved;
            try
            {
                resolved = resolver.ResolveAsync(model).GetAwaiter().GetResult();
            }
            catch
            {
                resolved = null;
            }

            if (resolved is null)
            {
                Console.Error.WriteLine("fuse: semantic embedding model unavailable; using hashing embeddings");
                return new HashingEmbeddingModel();
            }

            try
            {
                return new OnnxEmbeddingModel(resolved);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"fuse: failed to load semantic embedding model ({ex.Message}); using hashing embeddings");
                return new HashingEmbeddingModel();
            }
        });

        return services;
    }
}
