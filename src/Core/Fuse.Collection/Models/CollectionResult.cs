namespace Fuse.Collection.Models;

/// <summary>
///     Represents the outcome of a file collection run.
/// </summary>
/// <remarks>
///     Contains the files that passed all filters, sorted by descending file size,
///     along with the total number of candidates evaluated during the scan.
/// </remarks>
public sealed class CollectionResult
{
    /// <summary>
    ///     Gets the source files included in the collection result.
    /// </summary>
    public IReadOnlyList<SourceFile> Files { get; }

    /// <summary>
    ///     Gets the number of file candidates evaluated before filtering.
    /// </summary>
    public int CandidatesEvaluated { get; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="CollectionResult" /> class.
    /// </summary>
    /// <param name="files">The source files that passed all filters.</param>
    /// <param name="candidatesEvaluated">The number of candidates evaluated during enumeration.</param>
    public CollectionResult(IReadOnlyList<SourceFile> files, int candidatesEvaluated)
    {
        Files = files;
        CandidatesEvaluated = candidatesEvaluated;
    }
}
