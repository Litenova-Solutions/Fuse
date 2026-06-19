using Fuse.Collection;
using Fuse.Emission;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction;
using Fuse.Reduction.Tokenization;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion;

/// <summary>
///     Orchestrates the full fusion pipeline: collection, reduction, and emission.
/// </summary>
/// <remarks>
///     This class is stateless and safe to register as a singleton. Transient pipeline
///     services are resolved per invocation from the service provider.
/// </remarks>
public sealed class FusionOrchestrator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly FusionValidator _validator;
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionOrchestrator" /> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve transient pipeline services.</param>
    /// <param name="validator">The validator used before execution.</param>
    /// <param name="tokenCounter">The token counter used when creating output writers.</param>
    public FusionOrchestrator(
        IServiceProvider serviceProvider,
        FusionValidator validator,
        ITokenCounter tokenCounter)
    {
        _serviceProvider = serviceProvider;
        _validator = validator;
        _tokenCounter = tokenCounter;
    }

    /// <summary>
    ///     Executes the full fusion pipeline for the specified request.
    /// </summary>
    /// <param name="request">The fusion request to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="FusionResult" /> containing emission statistics and output references.</returns>
    /// <exception cref="FusionValidationException">Thrown when the request fails validation.</exception>
    public async Task<FusionResult> FuseAsync(FusionRequest request, CancellationToken cancellationToken = default)
    {
        _validator.ValidateOrThrow(request);

        var collectionPipeline = _serviceProvider.GetRequiredService<FileCollectionPipeline>();
        var reductionPipeline = _serviceProvider.GetRequiredService<ContentReductionPipeline>();
        var emissionPipeline = _serviceProvider.GetRequiredService<EmissionPipeline>();

        var collectionResult = await collectionPipeline.CollectAsync(request.Collection, cancellationToken);

        var reducedContent = await reductionPipeline.ReduceAsync(
            collectionResult.Files,
            request.Reduction,
            cancellationToken);

        IOutputWriter writer = request.InMemory
            ? new InMemoryOutputWriter(request.Emission, _tokenCounter)
            : new DiskOutputWriter(request.Emission, _tokenCounter);

        try
        {
            return await emissionPipeline.EmitAsync(
                reducedContent,
                request.Emission,
                writer,
                cancellationToken);
        }
        finally
        {
            if (writer is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }
}
