using Fuse.Fusion.Scoping;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Plugins.Abstractions.Options;

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
        ChangeOptions? changes = null,
        QueryOptions? query = null,
        int parallelism = 0,
        bool useReductionCache = true,
        bool clearReductionCache = false,
        bool usePersistentIndex = false)
    {
        Collection = collection;
        Reduction = reduction;
        Emission = emission;
        InMemory = inMemory;
        Focus = focus;
        Changes = changes;
        Query = query;
        Parallelism = parallelism;
        UseReductionCache = useReductionCache;
        ClearReductionCache = clearReductionCache;
        UsePersistentIndex = usePersistentIndex;
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

    /// <summary>
    ///     Gets BM25 query scoping options, or <c>null</c> when not query-scoped.
    /// </summary>
    public QueryOptions? Query { get; }

    /// <summary>
    ///     Gets the maximum degree of parallelism for pipeline stages.
    /// </summary>
    public int Parallelism { get; }

    /// <summary>
    ///     Gets a value indicating whether per-file reduction results are cached in the SQLite store
    ///     (<c>.fuse/fuse.db</c>).
    /// </summary>
    /// <remarks>
    ///     The database path is <c>{repoRoot}/.fuse/fuse.db</c> inside a git repository, or
    ///     <c>~/.fuse/fuse.db</c> (override with <c>FUSE_USER_DATA</c>) otherwise.
    /// </remarks>
    public bool UseReductionCache { get; }

    /// <summary>
    ///     Gets a value indicating whether cached reduction entries in <c>.fuse/fuse.db</c> are cleared
    ///     before fusion runs.
    /// </summary>
    public bool ClearReductionCache { get; }

    /// <summary>
    ///     Gets a value indicating whether per-file dependency and symbol analysis is cached in
    ///     <c>.fuse/fuse.db</c>, so repeated scoping calls reuse it. Off by default.
    /// </summary>
    /// <remarks>
    ///     The database path is <c>{repoRoot}/.fuse/fuse.db</c> inside a git repository, or
    ///     <c>~/.fuse/fuse.db</c> (override with <c>FUSE_USER_DATA</c>) otherwise.
    /// </remarks>
    public bool UsePersistentIndex { get; }
}
