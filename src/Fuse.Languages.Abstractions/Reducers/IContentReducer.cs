using Fuse.Languages.Abstractions.Options;

namespace Fuse.Languages.Abstractions.Reducers;

/// <summary>
///     Reduces file content for a specific file extension.
/// </summary>
public interface IContentReducer : ILanguageCapability
{
    /// <summary>
    ///     Applies extension-specific reduction to the supplied content.
    /// </summary>
    /// <param name="content">The normalized file content to reduce.</param>
    /// <param name="options">The reduction options for the current run.</param>
    /// <returns>The reduced content.</returns>
    string Reduce(string content, ReductionOptions options);
}
