using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Defines the contract for a file collection filter.
/// </summary>
/// <remarks>
///     Filters are evaluated in registration order by <see cref="FileCollectionPipeline" />.
///     A candidate is included only when every filter returns <c>true</c>.
/// </remarks>
public interface IFileFilter
{
    /// <summary>
    ///     Determines whether the specified file candidate should be included in the collection.
    /// </summary>
    /// <param name="candidate">The file candidate to evaluate.</param>
    /// <param name="options">The collection options for the current run.</param>
    /// <returns><c>true</c> if the candidate passes this filter; otherwise, <c>false</c>.</returns>
    bool Include(FileCandidate candidate, CollectionOptions options);
}
