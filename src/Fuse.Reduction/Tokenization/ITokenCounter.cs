namespace Fuse.Reduction.Tokenization;



/// <summary>

///     Counts tokens in text content for downstream emission and budgeting.

/// </summary>

public interface ITokenCounter

{

    /// <summary>

    ///     Returns the token count for the specified content.

    /// </summary>

    /// <param name="content">The text to count tokens in.</param>

    /// <returns>The number of tokens in <paramref name="content" />.</returns>

    int Count(string content);

}

