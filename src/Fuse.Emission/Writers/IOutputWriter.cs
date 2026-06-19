using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Writes fused content entries to an output destination and produces a <see cref="FusionResult" />.
/// </summary>
public interface IOutputWriter
{
    /// <summary>
    ///     Gets a value indicating whether this writer supports splitting output into multiple parts.
    /// </summary>
    bool SupportsMultiPart { get; }

    /// <summary>
    ///     Writes a single fused content entry to the output.
    /// </summary>
    /// <param name="content">The fused content entry to write.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Finalizes the current output part and begins a new part for split emission.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous rotation operation.</returns>
    Task RotatePartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Completes emission and returns the final result.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous completion, returning emission statistics.</returns>
    Task<FusionResult> CompleteAsync(CancellationToken cancellationToken = default);
}
