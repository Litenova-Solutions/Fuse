using System.Text;
using System.Text.RegularExpressions;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Extracts the comment and documentation-comment text from C-family source so it can be indexed as its own
///     weighted relevance field (Q2). Comments carry the natural-language vocabulary a prose query matches,
///     which is otherwise diluted across the body field.
/// </summary>
/// <remarks>
///     This is a lexical, best-effort extractor for ranking only, not a parser: it matches C-style line
///     (<c>//</c>) and block (<c>/* */</c>) comments with a regex, so a <c>//</c> sequence inside a string
///     literal is treated as a comment. That is acceptable for a relevance signal (it only affects which files
///     rank, never emitted content, which the redaction-correct reduction path still governs) and keeps the
///     extractor language-agnostic across the C-family sources Fuse scopes.
/// </remarks>
public static partial class CommentExtractor
{
    // Block comments first, then line comments. A line-comment alternative consumes to end-of-line, so a `/*`
    // inside a `//` line is not treated as a block open, and a `//` inside a `/* */` block is consumed by the
    // block match, so the two never overlap incorrectly.
    [GeneratedRegex(@"/\*[\s\S]*?\*/|//[^\r\n]*", RegexOptions.Compiled)]
    private static partial Regex CommentPattern();

    /// <summary>
    ///     Returns the concatenated comment text found in <paramref name="content" />, or an empty string when
    ///     there are no comments.
    /// </summary>
    /// <param name="content">The source text to scan.</param>
    /// <returns>The comment text, one comment per line, with comment markers left in place for tokenization.</returns>
    public static string Extract(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (Match match in CommentPattern().Matches(content))
        {
            builder.Append(match.Value);
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
