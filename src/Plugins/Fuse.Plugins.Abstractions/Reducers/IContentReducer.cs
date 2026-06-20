using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Plugins.Abstractions.Reducers;

/// <summary>
///     Reduces file content for a specific file extension, applying language-aware transformations
///     such as comment stripping, whitespace condensing, or structural compression.
/// </summary>
/// <remarks>
///     A reducer is resolved by file extension through <see cref="CapabilityRegistry{TCapability}" /> using
///     <see cref="ILanguageCapability.SupportedExtensions" />. Implementations must be pure and stateless:
///     reduction may run concurrently across files on multiple threads.
/// </remarks>
public interface IContentReducer : ILanguageCapability
{
    /// <summary>
    ///     Applies extension-specific reduction to the supplied content.
    /// </summary>
    /// <param name="content">
    ///     The normalized file content to reduce. Must not be <see langword="null" />; an empty string
    ///     yields an empty result.
    /// </param>
    /// <param name="options">The reduction options governing which transformations are applied for the current run.</param>
    /// <returns>
    ///     The reduced content. Returns the input unchanged when no enabled option applies to this reducer.
    /// </returns>
    string Reduce(string content, ReductionOptions options);
}
