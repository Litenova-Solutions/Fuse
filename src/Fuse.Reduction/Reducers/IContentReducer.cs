using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers;



/// <summary>

///     Reduces file content for a specific file extension.

/// </summary>

public interface IContentReducer

{

    /// <summary>

    ///     Gets the primary file extension this reducer handles, including the leading dot.

    /// </summary>

    string Extension { get; }



    /// <summary>

    ///     Applies extension-specific reduction to the supplied content.

    /// </summary>

    /// <param name="content">The normalized file content to reduce.</param>

    /// <param name="options">The reduction options for the current run.</param>

    /// <returns>The reduced content.</returns>

    string Reduce(string content, ReductionOptions options);

}

