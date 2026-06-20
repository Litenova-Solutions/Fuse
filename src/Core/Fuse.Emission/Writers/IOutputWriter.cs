using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Writes fused content entries to an output destination and produces a <see cref="FusionResult" />.
/// </summary>
/// <remarks>
///     The expected call sequence is: an optional <see cref="WritePrefixAsync" />, zero or more
///     <see cref="WriteEntryAsync" /> calls interleaved with <see cref="RotatePartAsync" /> when splitting,
///     then exactly one <see cref="CompleteAsync" />. After completion the writer is terminal: further write,
///     rotate, or complete calls throw <see cref="InvalidOperationException" />. Trivial entries
///     (<see cref="FusedContent.IsTrivial" />) are ignored. Implementations decide whether output is written
///     to disk or held in memory; see <see cref="DiskOutputWriter" /> and <see cref="InMemoryOutputWriter" />.
/// </remarks>
public interface IOutputWriter
{
    /// <summary>
    ///     A value indicating whether this writer supports splitting output into multiple parts via
    ///     <see cref="RotatePartAsync" />.
    /// </summary>
    bool SupportsMultiPart { get; }

    /// <summary>
    ///     Writes a single fused content entry to the output.
    /// </summary>
    /// <param name="content">The fused content entry to write. Ignored when trivial.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the entry has been written.</returns>
    /// <exception cref="InvalidOperationException">Thrown when called after <see cref="CompleteAsync" />.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is signalled.</exception>
    Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes content prepended before the first file entry (for example a manifest header).
    /// </summary>
    /// <param name="content">The prefix content to write. Empty content is a no-op.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the prefix has been written.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is signalled.</exception>
    Task WritePrefixAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Finalizes the current output part and begins a new part for split emission.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the part has been rotated.</returns>
    /// <remarks>
    ///     Writers that do not support multi-part output (<see cref="SupportsMultiPart" /> is <c>false</c>)
    ///     treat this as a no-op.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when called after <see cref="CompleteAsync" />.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is signalled.</exception>
    Task RotatePartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Completes emission and returns the final result.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A task whose result is the <see cref="FusionResult" /> describing the emitted output and statistics.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when emission has already completed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is signalled.</exception>
    Task<FusionResult> CompleteAsync(CancellationToken cancellationToken = default);
}
