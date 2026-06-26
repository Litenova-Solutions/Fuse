using Fuse.Collection;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Fusion.Stages;

/// <summary>
///     The collection stage of the fusion pipeline: discovers and filters candidate source files.
/// </summary>
public sealed class FusionCollectionStage
{
    private readonly FileCollectionPipeline _collectionPipeline;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionCollectionStage" /> class.
    /// </summary>
    /// <param name="collectionPipeline">The underlying collection pipeline.</param>
    public FusionCollectionStage(FileCollectionPipeline collectionPipeline)
    {
        _collectionPipeline = collectionPipeline;
    }

    /// <summary>
    ///     Collects candidate files for a fusion run.
    /// </summary>
    /// <param name="options">Collection options from the fusion request.</param>
    /// <param name="parallelism">Maximum degree of parallelism for collection.</param>
    /// <param name="cancellationToken">Token used to cancel collection.</param>
    /// <returns>The collection result containing discovered files.</returns>
    public Task<CollectionResult> CollectAsync(
        CollectionOptions options,
        int parallelism,
        CancellationToken cancellationToken = default) =>
        _collectionPipeline.CollectAsync(options, parallelism, cancellationToken);
}
