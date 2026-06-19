using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Reduction.Options;

namespace Fuse.Fusion;

/// <summary>
///     Represents a complete fusion request spanning collection, reduction, and emission.
/// </summary>
public sealed class FusionRequest
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionRequest" /> class.
    /// </summary>
    public FusionRequest(
        CollectionOptions collection,
        ReductionOptions reduction,
        EmissionOptions emission,
        bool inMemory = false,
        FocusOptions? focus = null,
        ChangeOptions? changes = null)
    {
        Collection = collection;
        Reduction = reduction;
        Emission = emission;
        InMemory = inMemory;
        Focus = focus;
        Changes = changes;
    }

    /// <summary>
    ///     Gets the collection options for file discovery and filtering.
    /// </summary>
    public CollectionOptions Collection { get; }

    /// <summary>
    ///     Gets the reduction options for content normalization and minification.
    /// </summary>
    public ReductionOptions Reduction { get; }

    /// <summary>
    ///     Gets the emission options for output generation and token budgeting.
    /// </summary>
    public EmissionOptions Emission { get; }

    /// <summary>
    ///     Gets a value indicating whether output is captured in memory instead of written to disk.
    /// </summary>
    public bool InMemory { get; }

    /// <summary>
    ///     Gets focus scoping options, or <c>null</c> when not scoped.
    /// </summary>
    public FocusOptions? Focus { get; }

    /// <summary>
    ///     Gets change scoping options, or <c>null</c> when not scoped by git changes.
    /// </summary>
    public ChangeOptions? Changes { get; }
}
